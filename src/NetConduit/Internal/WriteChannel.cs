using System.Buffers;
using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

internal sealed class WriteChannel : Stream, IWriteChannel
{
    private readonly byte[] _slab;
    private readonly Memory<byte> _slabMemory;
    private ushort _channelIndex;
    private readonly IChannelOwner _owner;

    internal ushort ChannelIndex => _channelIndex;
    private readonly SemaphoreSlim _spaceAvailable = new(0, 1);
    private readonly object _posLock = new();
    private readonly int _slabSize;
    private readonly TimeSpan _sendTimeout;
    private readonly bool _enableReplay;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unregisteredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _ackedPos;
    private int _sentPos;
    private int _pendingPos;
    private int _writePos;
    private long _compactionOffset;
    private bool _routerReading;
    private int _completionNotified;
    private int _slabReturned;
    private int _totalDataBytesStaged;
    private long _peerAckedBytes;
    private int _initFrameBytesInSlab;
    private bool _finRequested;
    private bool _finQueued;

    private volatile ChannelState _state = ChannelState.Opening;
    private volatile bool _isReady;
    private volatile bool _isConnected;
    private volatile bool _peerCapNegotiated;
    private int _connectedFired;
    private ChannelCloseReason? _closeReason;
    private Exception? _closeException;

    public string ChannelId { get; }
    public ChannelState State => _state;
    public bool IsReady => _isReady;
    public bool IsConnected => _isConnected;
    public ChannelPriority Priority { get; }
    public ChannelStats Stats { get; } = new();
    public ChannelCloseReason? CloseReason => _closeReason;
    public Exception? CloseException => _closeException;
    public event EventHandler? Ready;
    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public event EventHandler<ChannelCloseEventArgs>? Closed;

    public Stream AsStream() => this;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _state is ChannelState.Open or ChannelState.Opening;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    internal WriteChannel(string channelId, ushort channelIndex, ChannelPriority priority, int slabSize, TimeSpan sendTimeout, IChannelOwner owner, bool enableReplay = false)
    {
        ChannelId = channelId;
        _channelIndex = channelIndex;
        Priority = priority;
        _slabSize = slabSize;
        _sendTimeout = sendTimeout;
        _owner = owner;
        _enableReplay = enableReplay;
        _slab = ArrayPool<byte>.Shared.Rent(slabSize);
        _slabMemory = _slab.AsMemory();
    }

    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    internal void MarkOpen()
    {
        bool promoted;
        lock (_posLock)
        {
            if (_state != ChannelState.Opening) return;
            _state = ChannelState.Open;
            promoted = true;
        }
        if (promoted && !_isReady)
        {
            _isReady = true;
            SafeEventRaiser.Raise(this, Ready, _owner.NotifyEventHandlerException);
            _owner.NotifyChannelOpened(ChannelId);
            _readyTcs.TrySetResult();
        }
    }

    internal void MarkConnected()
    {
        if (Interlocked.Exchange(ref _connectedFired, 1) == 1) return;
        _isConnected = true;
        _peerCapNegotiated = true;
        SafeEventRaiser.Raise(this, Connected, _owner.NotifyEventHandlerException);
    }

    internal void MarkDisconnected(DisconnectReason reason, Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _connectedFired, 0) == 0) return;
        _isConnected = false;
        SafeEventRaiser.Raise(this, Disconnected, new DisconnectedEventArgs(reason, exception), _owner.NotifyEventHandlerException);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.IsEmpty) return;
        int offset = 0;
        while (offset < data.Length)
        {
            int written = await WriteFrameAsync(data[offset..], ct).ConfigureAwait(false);
            offset += written;
        }
    }

    private async ValueTask<int> WriteFrameAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_state is not (ChannelState.Open or ChannelState.Opening))
            throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

        var (effectiveSlab, maxPayload) = GetFrameBudget();
        int payloadLength = Math.Min(data.Length, maxPayload);
        int frameSize = FrameHeader.Size + payloadLength;

        while (true)
        {
            lock (_posLock)
            {
                TryCompactLocked();
                int localFree = _slabSize - _writePos;
                int outstanding = _writePos - _initFrameBytesInSlab;
                if (_peerAckedBytes > 0)
                {
                    int peerOutstanding = _totalDataBytesStaged - (int)_peerAckedBytes;
                    if (peerOutstanding > outstanding)
                        outstanding = peerOutstanding;
                }
                int peerFree = effectiveSlab - outstanding;
                if (Math.Min(localFree, peerFree) >= frameSize) break;
            }

            if (_state is not (ChannelState.Open or ChannelState.Opening))
                throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

            bool acquired;
            try { acquired = await _spaceAvailable.WaitAsync(_sendTimeout, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose); }

            if (!acquired)
            {
                if (_state is not (ChannelState.Open or ChannelState.Opening))
                    throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);
                throw new TimeoutException($"WriteChannel '{ChannelId}' timed out waiting for slab space.");
            }
        }

        lock (_posLock)
        {
            if (_state is not (ChannelState.Open or ChannelState.Opening))
                throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Data, payloadLength);
            data.Span[..payloadLength].CopyTo(_slab.AsSpan(frameStart + FrameHeader.Size, payloadLength));
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
            _totalDataBytesStaged += frameSize;
        }

        Interlocked.Add(ref Stats._bytesSent, payloadLength);
        Interlocked.Increment(ref Stats._framesSent);
        _owner.NotifyReady(this);
        return payloadLength;
    }

    private (int EffectiveSlab, int MaxPayload) GetFrameBudget()
    {
        int peerMaxRecvPayload = _peerCapNegotiated ? _owner.PeerMaxRecvPayload : FrameConstants.MinSlabSize;
        int effectiveSlab = Math.Min(_slabSize, peerMaxRecvPayload);
        int maxPayload = Math.Min(effectiveSlab - FrameHeader.Size, FrameConstants.MaxFramePayloadSize);
        if (maxPayload <= 0)
            throw new InvalidOperationException($"Channel '{ChannelId}' has no usable per-frame payload budget.");
        return (effectiveSlab, maxPayload);
    }

    internal void WriteInitFrame(ReadOnlySpan<byte> channelIdUtf8)
    {
        int frameSize = FrameHeader.Size + channelIdUtf8.Length;
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frameSize)
                throw new InvalidOperationException("Slab full for INIT frame.");
            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Init, channelIdUtf8.Length);
            channelIdUtf8.CopyTo(_slab.AsSpan(frameStart + FrameHeader.Size, channelIdUtf8.Length));
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
            _initFrameBytesInSlab = frameSize;
        }
        _owner.NotifyReady(this);
    }

    internal void RestampChannelIndex(ushort newIndex)
    {
        lock (_posLock)
        {
            int pos = _sentPos;
            while (pos + FrameHeader.Size <= _writePos)
            {
                BinaryPrimitives.WriteUInt16BigEndian(_slab.AsSpan(pos, 2), newIndex);
                uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(_slab.AsSpan(pos + 4, 4));
                pos += FrameHeader.Size + (int)payloadLength;
            }
            _channelIndex = newIndex;
        }
    }

    internal void WriteAckFrame(ulong receivedPosition)
    {
        int frameSize = FrameHeader.Size + 8;
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frameSize)
                throw new InvalidOperationException("Slab full for ACK frame.");
            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Ack, 8);
            BinaryPrimitives.WriteUInt64BigEndian(_slab.AsSpan(frameStart + FrameHeader.Size, 8), receivedPosition);
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
        }
        _owner.NotifyReady(this);
    }

    internal void WriteFinFrame()
    {
        int frameSize = FrameHeader.Size;
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frameSize)
                throw new InvalidOperationException("Slab full for FIN frame.");
            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Fin, 0);
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
        }
        _owner.NotifyReady(this);
    }

    internal void WriteRawFrame(ReadOnlySpan<byte> frame)
    {
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frame.Length)
                throw new InvalidOperationException("Slab full for raw frame.");
            frame.CopyTo(_slab.AsSpan(_writePos, frame.Length));
            _writePos += frame.Length;
            _pendingPos = _writePos;
        }
        _owner.NotifyReady(this);
    }

    internal bool TryWriteRawFrame(ReadOnlySpan<byte> frame)
    {
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frame.Length)
                return false;
            frame.CopyTo(_slab.AsSpan(_writePos, frame.Length));
            _writePos += frame.Length;
            _pendingPos = _writePos;
        }
        _owner.NotifyReady(this);
        return true;
    }

    internal Memory<byte> TakeReady()
    {
        lock (_posLock)
        {
            if (_pendingPos <= _sentPos) return Memory<byte>.Empty;
            _routerReading = true;
            return _slabMemory[_sentPos.._pendingPos];
        }
    }

    internal void MarkSent(int bytes)
    {
        bool finQueued;
        bool finDrained;
        lock (_posLock)
        {
            _routerReading = false;
            _sentPos += bytes;
            if (!_enableReplay)
                _ackedPos = _sentPos;
            else if (_finRequested && _enableReplay)
                _ackedPos = _sentPos;
            finQueued = TryQueuePendingFinLocked();
            finDrained = (_state is ChannelState.Closing or ChannelState.Closed) && _finQueued && _pendingPos <= _sentPos;
        }
        TryReleaseSpaceSignal();
        if (finQueued) _owner.NotifyReady(this);
        if (finDrained) SetClosed(ChannelCloseReason.LocalClose);
        else TryNotifyCompleted();
    }

    internal void OnAck(long ackedPosition)
    {
        bool finQueued;
        lock (_posLock)
        {
            long upper = _compactionOffset + _writePos;
            if (ackedPosition > upper) ackedPosition = upper;
            int slabRelative = (int)(ackedPosition - _compactionOffset);
            if (slabRelative > _ackedPos) _ackedPos = slabRelative;
            if (ackedPosition > _peerAckedBytes) _peerAckedBytes = ackedPosition;
            finQueued = TryQueuePendingFinLocked();
        }
        if (!_isReady) MarkOpen();
        TryReleaseSpaceSignal();
        if (finQueued) _owner.NotifyReady(this);
    }

    internal void PrepareReplay() { lock (_posLock) { _sentPos = _ackedPos; } _owner.NotifyReady(this); }

    internal void SetReplayBase(long peerReceivedPosition)
    {
        lock (_posLock)
        {
            long upper = _compactionOffset + _sentPos;
            if (peerReceivedPosition > upper) peerReceivedPosition = upper;
            int slabRelative = (int)(peerReceivedPosition - _compactionOffset);
            if (slabRelative > _ackedPos) _ackedPos = slabRelative;
        }
        TryReleaseSpaceSignal();
    }

    internal bool HasPendingData() { lock (_posLock) { return _pendingPos > _sentPos; } }

    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        bool finQueued;
        lock (_posLock)
        {
            if (_state is ChannelState.Closing or ChannelState.Closed)
                return ValueTask.CompletedTask;
            _state = ChannelState.Closing;
            _finRequested = true;
            finQueued = TryQueuePendingFinLocked();
        }
        if (finQueued) _owner.NotifyReady(this);
        return ValueTask.CompletedTask;
    }

    private bool TryQueuePendingFinLocked()
    {
        if (!_finRequested || _finQueued || _state is not (ChannelState.Closing or ChannelState.Closed))
            return false;
        TryCompactLocked();
        if (_slabSize - _writePos < FrameHeader.Size)
            return false;
        int frameStart = _writePos;
        FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Fin, 0);
        _writePos = frameStart + FrameHeader.Size;
        _pendingPos = _writePos;
        _finQueued = true;
        return true;
    }

    private bool TryWriteFinFrameSafe()
    {
        try { WriteFinFrame(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal void SetClosed(ChannelCloseReason reason, Exception? exception = null)
    {
        lock (_posLock)
        {
            if (_state == ChannelState.Closed) return;
            _state = ChannelState.Closed;
            _closeReason = reason;
            _closeException = exception;
            _isConnected = false;
        }
        _readyTcs.TrySetException(new ChannelClosedException(ChannelId, reason));
        TryReleaseSpaceSignal();
        SafeEventRaiser.Raise(this, Closed, new ChannelCloseEventArgs(reason, exception), _owner.NotifyEventHandlerException);
        if (reason != ChannelCloseReason.MuxDisposed) { TryNotifyCompleted(); }
        else { lock (_posLock) TryReturnSlab(); _unregisteredTcs.TrySetResult(); }
    }

    internal void Abort(ChannelCloseReason reason, Exception? exception = null)
    {
        SetClosed(reason, exception);
        lock (_posLock) TryReturnSlab();
        _spaceAvailable.Dispose();
    }

    private void TryReturnSlab() { if (Interlocked.CompareExchange(ref _slabReturned, 1, 0) != 0) return; ArrayPool<byte>.Shared.Return(_slab); }

    private void TryNotifyCompleted()
    {
        if (ChannelId is null) { _unregisteredTcs.TrySetResult(); return; }
        if (_state != ChannelState.Closed) return;
        if (Interlocked.CompareExchange(ref _completionNotified, 1, 0) == 0) { _owner.NotifyChannelCompleted(_channelIndex, ChannelId); _unregisteredTcs.TrySetResult(); }
        if (!HasPendingData() || _closeReason is ChannelCloseReason.MuxDisposed or ChannelCloseReason.TransportFailed)
            lock (_posLock) TryReturnSlab();
    }

    private void TryReleaseSpaceSignal()
    {
        try { if (_spaceAvailable.CurrentCount == 0) _spaceAvailable.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private void TryCompactLocked()
    {
        if (_ackedPos <= 0 || _routerReading) return;
        int acked = _ackedPos;
        int unackedLength = _writePos - acked;
        if (unackedLength > 0)
            _slab.AsSpan(acked, unackedLength).CopyTo(_slab.AsSpan(0, unackedLength));
        if (_initFrameBytesInSlab > 0 && acked >= _initFrameBytesInSlab)
            _initFrameBytesInSlab = 0;
        _compactionOffset += acked;
        _sentPos -= acked;
        _pendingPos -= acked;
        _writePos -= acked;
        _ackedPos = 0;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        if (_state is not ChannelState.Closed) { await CloseAsync(); SetClosed(ChannelCloseReason.LocalClose); }
        await _unregisteredTcs.Task.ConfigureAwait(false);
        _spaceAvailable.Dispose();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _state is not ChannelState.Closed)
        {
            bool queueFin = false;
            lock (_posLock) { if (_state is ChannelState.Opening or ChannelState.Open) { _state = ChannelState.Closing; queueFin = true; } }
            if (queueFin) TryWriteFinFrameSafe();
            SetClosed(ChannelCloseReason.LocalClose);
            _spaceAvailable.Dispose();
        }
        base.Dispose(disposing);
    }
}