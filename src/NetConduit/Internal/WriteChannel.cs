using System.Buffers;
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
    private readonly IChannelOwner _owner;

    internal ushort ChannelIndex => _channelIndex;
    private readonly SemaphoreSlim _spaceAvailable = new(0, 1);
    private readonly object _posLock = new();
    private readonly int _slabSize;
    private readonly TimeSpan _sendTimeout;
    private readonly bool _enableReplay;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Completes once the channel has been removed from the owner's registry,
    // so DisposeAsync can guarantee the channel ID is reusable when it returns.
    private readonly TaskCompletionSource _unregisteredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Position tracking — the slab is a linear buffer with compaction
    // All position fields are protected by _posLock
    private int _ackedPos;
    private int _sentPos;
    private int _pendingPos;
    private int _writePos;
    private long _compactionOffset; // cumulative bytes compacted away (converts global ACK to slab-relative)
    private bool _routerReading; // true between TakeReady and MarkSent — blocks compaction
    private int _completionNotified; // CAS guard: ensures NotifyChannelCompleted fires exactly once
    private int _slabReturned; // CAS guard: ensures slab is returned to pool exactly once

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
        IChannelOwner owner,
        bool enableReplay = false)
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
            // Raise synchronous notifications first so handlers observe a ready channel,
            // then complete the TCS so async awaiters resume only after handlers ran.
            Ready?.Invoke(this, EventArgs.Empty);
            _owner.NotifyChannelOpened(ChannelId);
            _readyTcs.TrySetResult();
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

        // A single frame must fit in the slab. There is no chunking and no
        // possible state where a payload larger than the slab can be queued,
        // so fail fast instead of stalling for the full SendTimeout.
        int maxPayload = _slabSize - FrameHeader.Size;
        if (payloadLength > maxPayload)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Payload of {payloadLength} bytes exceeds the per-frame budget of {maxPayload} bytes for channel '{ChannelId}' (slab size {_slabSize}). " +
                $"Configure a larger ChannelOptions.SlabSize or split the payload before writing.");
        }

        // Wait for space in the slab if needed
        while (true)
        {
            lock (_posLock)
            {
                TryCompactLocked();
                if (_slabSize - _writePos >= frameSize) break;
            }

            // Re-check state before re-parking. SetClosed/Abort wake parked
            // writers via TryReleaseSpaceSignal precisely so they can unwind
            // here with ChannelClosedException instead of stalling for the
            // full SendTimeout or surfacing ObjectDisposedException when the
            // semaphore is disposed by Abort (issue #230).
            if (_state is not (ChannelState.Open or ChannelState.Opening))
                throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

            bool acquired;
            try
            {
                acquired = await _spaceAvailable.WaitAsync(_sendTimeout, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);
            }

            if (!acquired)
            {
                // Distinguish "channel closed during the wait" from "no ACK
                // arrived in time" — the close signal and the timeout race,
                // and the caller should see ChannelClosedException whenever
                // the close happened concurrently.
                if (_state is not (ChannelState.Open or ChannelState.Opening))
                    throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);
                throw new TimeoutException($"WriteChannel '{ChannelId}' timed out waiting for slab space.");
            }
        }

        // Build the complete frame in the slab (under lock to prevent races with TakeReady).
        // Re-check state INSIDE the lock to prevent the slab-use-after-free race in #258:
        // a concurrent SetClosed/Abort can flip _state to Closed and return _slab to the
        // ArrayPool while a writer is parked on _spaceAvailable.WaitAsync. Once the writer
        // wakes (the close path releases the semaphore), the entry-state check at the top
        // of WriteAsync no longer holds. SetClosed/Abort/TryNotifyCompleted now serialize
        // their TryReturnSlab call under _posLock, so observing _state == Open here means
        // the slab is still ours for the duration of this critical section.
        lock (_posLock)
        {
            if (_state is not (ChannelState.Open or ChannelState.Opening))
                throw new ChannelClosedException(ChannelId, _closeReason ?? ChannelCloseReason.LocalClose);

            int frameStart = _writePos;
            FrameHeader.WriteTo(_slab.AsSpan(frameStart, FrameHeader.Size), _channelIndex, FrameFlags.Data, payloadLength);
            data.Span.CopyTo(_slab.AsSpan(frameStart + FrameHeader.Size, payloadLength));
            _writePos = frameStart + frameSize;
            _pendingPos = _writePos;
        }

        Interlocked.Add(ref Stats._bytesSent, payloadLength);
        Interlocked.Increment(ref Stats._framesSent);

        _owner.NotifyReady(this);
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
        _owner.NotifyReady(this);
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
        bool finDrained;
        lock (_posLock)
        {
            _routerReading = false;
            _sentPos += bytes;
            if (!_enableReplay)
            {
                // Without reconnection replay, treat sent as acked to free slab space
                _ackedPos = _sentPos;
            }
            // If a graceful close queued a FIN, finalize the Closing -> Closed
            // transition only after the writer thread has actually drained
            // everything (including the FIN). Synchronously finalizing inside
            // CloseAsync would unregister the channel before queued data frames
            // reach the wire and the peer would never receive them.
            finDrained = _state == ChannelState.Closing && _pendingPos <= _sentPos;
        }
        // Wake any blocked writer waiting for space
        TryReleaseSpaceSignal();

        if (finDrained)
        {
            SetClosed(ChannelCloseReason.LocalClose);
        }
        else
        {
            TryNotifyCompleted();
        }
    }

    internal void OnAck(long ackedPosition)
    {
        lock (_posLock)
        {
            // Clamp against the slab high-water mark to defend against a buggy
            // or hostile peer that ACKs a position beyond what was written.
            // Without this clamp, a single oversized ACK frame corrupts every
            // slab position field and the next WriteAsync throws
            // ArgumentOutOfRangeException from slab indexing.
            //
            // _writePos is the upper bound — not _sentPos — because the writer
            // thread can lag behind the local OnAck under high concurrency:
            // ACK arrives before MarkSent updates _sentPos. Clamping against
            // _sentPos would silently down-clamp legitimate ACKs and stall
            // compaction, deadlocking WriteAsync at slab full.
            long upper = _compactionOffset + _writePos;
            if (ackedPosition > upper)
                ackedPosition = upper;

            int slabRelative = (int)(ackedPosition - _compactionOffset);
            if (slabRelative > _ackedPos)
                _ackedPos = slabRelative;
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
        _owner.NotifyReady(this);
    }

    internal bool HasPendingData()
    {
        lock (_posLock)
        {
            return _pendingPos > _sentPos;
        }
    }

    /// <summary>
    /// Gracefully close this channel by queueing a FIN frame.
    /// Transitions the channel through Closing to Closed once the writer
    /// thread has drained the FIN (at which point the <see cref="Closed"/>
    /// event fires and the channel ID is released). The FIN frame itself is
    /// best-effort: if the slab cannot fit it, the channel finalizes locally
    /// and the peer observes closure when the multiplexer tears down.
    /// </summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (_state is ChannelState.Closing or ChannelState.Closed)
            return ValueTask.CompletedTask;

        _state = ChannelState.Closing;
        bool finQueued = TryWriteFinFrameSafe();
        // If the FIN could not be queued (slab full, or the writer thread is
        // not running) the caller must still observe a transition to Closed
        // so the channel ID is released and Closed event fires. Finalize
        // synchronously in that case.
        if (!finQueued)
        {
            SetClosed(ChannelCloseReason.LocalClose);
        }
        return ValueTask.CompletedTask;
    }

    // Best-effort FIN emit. The slab can be at capacity if the writer thread
    // is not running (e.g., handshake in flight) or if the caller filled the
    // slab. In that case the peer will still observe channel closure when the
    // multiplexer tears the transport down — the dispose path must not throw.
    // Returns true when the FIN was queued in the slab, false on slab-full.
    private bool TryWriteFinFrameSafe()
    {
        try
        {
            WriteFinFrame();
            return true;
        }
        catch (InvalidOperationException)
        {
            // Slab full — fall back to a best-effort skip.
            return false;
        }
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

        // When closed by AbortAllChannels (MuxDisposed), the registry handles cleanup via Clear().
        // Only self-unregister for graceful per-channel closes where MarkSent may have missed the window.
        if (reason != ChannelCloseReason.MuxDisposed)
        {
            TryNotifyCompleted();
        }
        else
        {
            // Writer loop is cancelled under MuxDisposed; no concurrent
            // TakeReady/MarkSent can race with us, so the slab must be
            // returned unconditionally. Gating on !HasPendingData() leaks
            // the slab whenever the channel is torn down with bytes still
            // queued (see issue #169). Serialize with _posLock so any
            // in-flight WriteAsync commit completes before the slab is
            // released to the pool (issue #258).
            lock (_posLock) TryReturnSlab();
            // Registry cleared externally on mux dispose; unblock any DisposeAsync awaiters.
            _unregisteredTcs.TrySetResult();
        }
    }

    internal void Abort(ChannelCloseReason reason, Exception? exception = null)
    {
        SetClosed(reason, exception);
        // Serialize with _posLock so any in-flight WriteAsync commit
        // completes before the slab is released to the pool (issue #258).
        lock (_posLock) TryReturnSlab();
        _spaceAvailable.Dispose();
    }

    private void TryReturnSlab()
    {
        if (Interlocked.CompareExchange(ref _slabReturned, 1, 0) != 0) return;
        ArrayPool<byte>.Shared.Return(_slab);
    }

    private void TryNotifyCompleted()
    {
        if (ChannelId is null)
        {
            // Never registered with the owner; nothing to unregister, unblock DisposeAsync.
            _unregisteredTcs.TrySetResult();
            return;
        }
        if (_state != ChannelState.Closed) return;
        // Unregister exactly once. Channel ID becomes reusable immediately on close,
        // regardless of whether the writer thread has finished draining queued frames.
        // This prevents DisposeAsync from blocking when the writer loop is not running
        // (e.g., the mux is still in handshake and will never drain the FIN).
        if (Interlocked.CompareExchange(ref _completionNotified, 1, 0) == 0)
        {
            _owner.NotifyChannelCompleted(_channelIndex, ChannelId);
            _unregisteredTcs.TrySetResult();
        }
        // Slab return is deferred until the writer has drained any in-flight frames,
        // so it stays valid for any concurrent TakeReady/MarkSent in progress.
        // Under terminal reasons (MuxDisposed / TransportFailed) the writer
        // loop is cancelled and will never drain; in those cases the slab
        // must be returned unconditionally or it leaks (issue #169).
        if (!HasPendingData()
            || _closeReason is ChannelCloseReason.MuxDisposed
                            or ChannelCloseReason.TransportFailed)
        {
            // Serialize with _posLock so any in-flight WriteAsync commit
            // completes before the slab is released to the pool (issue #258).
            lock (_posLock) TryReturnSlab();
        }
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

        _compactionOffset += acked;
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
        // Wait until the writer thread has drained the FIN frame and the channel
        // has been removed from the owner's registry, so the channel ID is
        // immediately reusable after DisposeAsync completes.
        await _unregisteredTcs.Task.ConfigureAwait(false);
        _spaceAvailable.Dispose();
        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _state is not ChannelState.Closed)
        {
            // Send FIN so the peer observes EOF, matching DisposeAsync semantics.
            // Best-effort: if the slab cannot fit FIN, finalize the close anyway
            // so Dispose never throws under normal conditions.
            if (_state is not ChannelState.Closing)
            {
                _state = ChannelState.Closing;
                TryWriteFinFrameSafe();
            }
            SetClosed(ChannelCloseReason.LocalClose);
            _spaceAvailable.Dispose();
        }
        base.Dispose(disposing);
    }
}
