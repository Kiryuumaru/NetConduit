using System.Buffers.Binary;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Outbound channel that owns a send slab and builds complete frames.
/// The channel does ALL heavy lifting: header stamping, flow control, reconnection replay.
/// The mux just picks up ready frames and sends them.
/// </summary>
internal sealed class WriteChannel : Stream, IWriteChannel
{
    private readonly byte[] _slab;
    private readonly Memory<byte> _slabMemory;
    private readonly ushort _channelIndex;
    private readonly IFrameRouter _router;
    private readonly SemaphoreSlim _spaceAvailable = new(0, 1);
    private readonly object _posLock = new();
    private readonly int _slabSize;
    private readonly TimeSpan _sendTimeout;
    private readonly bool _enableReplay;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Position tracking — the slab is a linear buffer with compaction
    // All position fields are protected by _posLock
    private int _ackedPos;
    private int _sentPos;
    private int _pendingPos;
    private int _writePos;
    private bool _routerReading; // true between TakeReady and MarkSent — blocks compaction

    private volatile ChannelState _state = ChannelState.Opening;
    private volatile bool _isReady;
    private volatile bool _isConnected;
    private ChannelCloseReason? _closeReason;
    private Exception? _closeException;

    /// <summary>The string identifier for this channel.</summary>
    public string ChannelId { get; }

    /// <summary>Current lifecycle state.</summary>
    public ChannelState State => _state;

    /// <summary>True after the channel has been confirmed by the remote side. Stays true forever.</summary>
    public bool IsReady => _isReady;

    /// <summary>True when the underlying transport is active. False during disconnects/reconnection.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Priority level used by the writer thread for ordering.</summary>
    public ChannelPriority Priority { get; }

    /// <summary>Per-channel statistics.</summary>
    public ChannelStats Stats { get; } = new();

    /// <summary>Reason the channel was closed, if applicable.</summary>
    public ChannelCloseReason? CloseReason => _closeReason;

    /// <summary>Exception that caused the close, if applicable.</summary>
    public Exception? CloseException => _closeException;

    /// <summary>Raised once when the channel first becomes ready. Never fires again.</summary>
    public event EventHandler? Ready;

    /// <summary>Raised each time the channel's underlying transport connects (including reconnects).</summary>
    public event EventHandler? Connected;

    /// <summary>Raised each time the channel's underlying transport disconnects.</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised when the channel is closed.</summary>
    public event EventHandler<ChannelCloseEventArgs>? Closed;

    // Stream overrides

    /// <inheritdoc />
    public Stream AsStream() => this;

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => _state is ChannelState.Open or ChannelState.Opening;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal WriteChannel(
        string channelId,
        ushort channelIndex,
        ChannelPriority priority,
        int slabSize,
        TimeSpan sendTimeout,
        IFrameRouter router,
        bool enableReplay = false)
    {
        ChannelId = channelId;
        _channelIndex = channelIndex;
        Priority = priority;
        _slabSize = slabSize;
        _sendTimeout = sendTimeout;
        _router = router;
        _enableReplay = enableReplay;

        _slab = GC.AllocateArray<byte>(slabSize, pinned: true);
        _slabMemory = _slab.AsMemory();
    }

    /// <summary>Wait until the channel is confirmed ready by the remote side.</summary>
    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    internal void MarkOpen()
    {
        // Don't regress state if channel is already closing or closed
        if (_state is ChannelState.Closing or ChannelState.Closed) return;

        _state = ChannelState.Open;
        if (!_isReady)
        {
            _isReady = true;
            _readyTcs.TrySetResult();
            Ready?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void MarkConnected()
    {
        _isConnected = true;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    internal void MarkDisconnected(DisconnectReason reason, Exception? exception = null)
    {
        _isConnected = false;
        Disconnected?.Invoke(this, new DisconnectedEventArgs(reason, exception));
    }

    /// <summary>
    /// Writes data to the channel. Builds a complete frame (header + payload) in the slab.
    /// This runs on the caller's thread, concurrent with other channels.
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_state is not (ChannelState.Open or ChannelState.Opening))
            throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

        int payloadLength = data.Length;
        if (payloadLength == 0) return;

        int frameSize = FrameHeader.Size + payloadLength;

        // Wait for space in the slab if needed
        while (true)
        {
            lock (_posLock)
            {
                TryCompactLocked();
                if (_slabSize - _writePos >= frameSize) break;
            }
            if (!await _spaceAvailable.WaitAsync(_sendTimeout, ct))
                throw new TimeoutException($"WriteChannel '{ChannelId}' timed out waiting for slab space.");
        }

        // Build the complete frame in the slab (under lock to prevent races with TakeReady)
        lock (_posLock)
        {
            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Data, payloadLength);
            data.Span.CopyTo(_slab.AsSpan(frameStart + FrameHeader.Size, payloadLength));
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
        }

        Interlocked.Add(ref Stats._bytesSent, payloadLength);
        Interlocked.Increment(ref Stats._framesSent);

        _router.NotifyReady(this);
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
        }
        _router.NotifyReady(this);
    }

    internal void WriteAckFrame(uint receivedPosition)
    {
        int frameSize = FrameHeader.Size + 4;
        lock (_posLock)
        {
            TryCompactLocked();
            if (_slabSize - _writePos < frameSize)
                throw new InvalidOperationException("Slab full for ACK frame.");

            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Ack, 4);
            BinaryPrimitives.WriteUInt32BigEndian(_slab.AsSpan(frameStart + FrameHeader.Size, 4), receivedPosition);
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
        }
        _router.NotifyReady(this);
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
        _router.NotifyReady(this);
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
        _router.NotifyReady(this);
    }

    // Called by mux writer thread — returns a Memory<byte> slice of ready-to-send frames
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
        lock (_posLock)
        {
            _routerReading = false;
            _sentPos += bytes;
            if (!_enableReplay)
            {
                // Without reconnection replay, treat sent as acked to free slab space
                _ackedPos = _sentPos;
            }
        }
        // Wake any blocked writer waiting for space
        TryReleaseSpaceSignal();
    }

    internal void OnAck(int ackedPosition)
    {
        lock (_posLock)
        {
            _ackedPos = ackedPosition;
        }
        // First ACK from remote confirms channel is open
        if (!_isReady)
            MarkOpen();
        // Wake any blocked writer waiting for space
        TryReleaseSpaceSignal();
    }

    internal void PrepareReplay()
    {
        lock (_posLock)
        {
            _sentPos = _ackedPos;
        }
        _router.NotifyReady(this);
    }

    internal bool HasPendingData()
    {
        lock (_posLock)
        {
            return _pendingPos > _sentPos;
        }
    }

    /// <summary>
    /// Gracefully close this channel by sending a FIN frame.
    /// </summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (_state is ChannelState.Closing or ChannelState.Closed)
            return ValueTask.CompletedTask;

        _state = ChannelState.Closing;
        WriteFinFrame();
        return ValueTask.CompletedTask;
    }

    internal void SetClosed(ChannelCloseReason reason, Exception? exception = null)
    {
        if (_state == ChannelState.Closed) return;
        _state = ChannelState.Closed;
        _closeReason = reason;
        _closeException = exception;
        _isConnected = false;
        // Wake anyone waiting for ready (channel will never open)
        _readyTcs.TrySetException(new ChannelClosedException(ChannelId, reason));
        // Wake any blocked writers
        TryReleaseSpaceSignal();
        Closed?.Invoke(this, new ChannelCloseEventArgs(reason, exception));
    }

    private void TryReleaseSpaceSignal()
    {
        try
        {
            if (_spaceAvailable.CurrentCount == 0)
                _spaceAvailable.Release();
        }
        catch (ObjectDisposedException) { /* disposed during shutdown */ }
        catch (SemaphoreFullException) { /* already signaled */ }
    }

    // Must be called under _posLock
    private void TryCompactLocked()
    {
        if (_ackedPos <= 0 || _routerReading) return;

        int acked = _ackedPos;
        int unackedLength = _writePos - acked;
        if (unackedLength > 0)
        {
            _slab.AsSpan(acked, unackedLength).CopyTo(_slab.AsSpan(0, unackedLength));
        }

        _sentPos -= acked;
        _pendingPos -= acked;
        _writePos -= acked;
        _ackedPos = 0;
    }

    // Stream plumbing
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_state is not ChannelState.Closed)
        {
            await CloseAsync();
            SetClosed(ChannelCloseReason.LocalClose);
        }
        _spaceAvailable.Dispose();
        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _state is not ChannelState.Closed)
        {
            SetClosed(ChannelCloseReason.LocalClose);
        }
        base.Dispose(disposing);
    }
}
