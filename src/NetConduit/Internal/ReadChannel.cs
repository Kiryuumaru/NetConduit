using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks.Sources;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Inbound channel that owns a receive slab and handles all receive logic.
/// Supports direct delivery (user already waiting) and slab buffering (user reads later).
/// </summary>
internal sealed class ReadChannel : Stream, IReadChannel, IValueTaskSource<int>
{
    private readonly byte[] _slab;
    private readonly Memory<byte> _slabMemory;
    private ushort _channelIndex;
    private readonly int _slabSize;
    private readonly object _lock = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IChannelOwner? _owner;
    // Cached method-group target for SafeEventRaiser so the channel event raise
    // sites can pass a single Action<Exception>? without re-checking the
    // nullable owner each time (#286). Null when the channel was constructed
    // without an owner (some test/internal contexts).
    private readonly Action<Exception>? _onHandlerException;
    private int _slabReturned; // CAS guard: ensures slab is returned to pool exactly once

    internal ushort ChannelIndex => _channelIndex;

    private int _receivedPos;
    private int _consumedPos;

    // Cumulative frame bytes (FrameHeader.Size + payload.Length) ever received,
    // counted post-skip. Mirrors the writer's slab position so an ACK that
    // reports this value can be applied directly as a slab position.
    private long _frameBytesReceived;
    private long _ackSentFrameBytes;

    // Replay skip: on reconnect, the writer replays from its last ACKed slab
    // position. The reader skips the frame bytes that arrived after the last
    // ACK so it doesn't re-deliver them to the user.
    private long _skipFrameBytes;

    // Direct delivery state
    private Memory<byte>? _pendingUserBuffer;
    private readonly ValueTaskCompletionSource _readCompletion = new();
    private bool _readCompletionActive;
    // Cancellation registration tied to the in-flight slow-path read.
    // Stored so completion paths can unregister it and avoid an unbounded
    // callback list on long-lived CancellationTokenSources. Reset to
    // default whenever _readCompletionActive transitions to false.
    private CancellationTokenRegistration _readCancelReg;

    private WriteChannel? _ackChannel;

    private volatile ChannelState _state = ChannelState.Opening;
    private volatile bool _isReady;
    private volatile bool _isConnected;
    private ChannelCloseReason? _closeReason;
    private Exception? _closeException;
    // CAS guard: ensures the stats decrement and ChannelClosed event fire exactly once,
    // regardless of whether the close path is the inbound FIN dispatcher or
    // local DisposeAsync/Dispose. Issue #172.
    private int _completionAccounted;

    /// <summary>
    /// Atomically claims the right to perform the one-shot close accounting
    /// (stats decrement and <see cref="StreamMultiplexer.ChannelClosed"/>
    /// event raise). Returns <c>true</c> exactly once across the lifetime of
    /// the channel; subsequent callers receive <c>false</c>.
    /// </summary>
    internal bool TryClaimCompletionAccounting() =>
        Interlocked.CompareExchange(ref _completionAccounted, 1, 0) == 0;

    /// <summary>The string identifier for this channel.</summary>
    public string ChannelId { get; }

    /// <summary>Current lifecycle state.</summary>
    public ChannelState State => _state;

    /// <summary>True after the channel has been confirmed by the remote side. Stays true forever.</summary>
    public bool IsReady => _isReady;

    /// <summary>True when the underlying transport is active. False during disconnects/reconnection.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Priority level of this channel.</summary>
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
    public override bool CanRead => _state is ChannelState.Open or ChannelState.Opening;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal ReadChannel(
        string channelId,
        ushort channelIndex,
        ChannelPriority priority,
        int slabSize,
        IChannelOwner? owner = null)
    {
        ChannelId = channelId;
        _channelIndex = channelIndex;
        Priority = priority;
        _slabSize = slabSize;
        _owner = owner;
        _onHandlerException = owner is null ? null : owner.NotifyEventHandlerException;

        _slab = ArrayPool<byte>.Shared.Rent(slabSize);
        _slabMemory = _slab.AsMemory();
    }

    /// <summary>Wait until the channel is confirmed ready by the remote side.</summary>
    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    internal void MarkOpen()
    {
        // Refuse to revive a Closed channel. A pending-accept channel that was
        // disposed before the peer's INIT arrived has already returned its slab
        // to ArrayPool<byte>.Shared; transitioning back to Open would let the
        // next DATA frame write into pooled memory the channel no longer owns
        // (use-after-free). The dispatcher detects this state under AcceptLock
        // and falls through to creating a fresh channel; this is the
        // defense-in-depth backstop.
        // Promote Opening → Open atomically under _lock so a concurrent
        // SetClosed cannot land between the check and the write and leave the
        // channel resurrected to Open after its slab/handlers were torn down
        // (issue #163). Closing/Closed/Open are all no-ops; only Opening promotes.
        bool promoted;
        lock (_lock)
        {
            if (_state != ChannelState.Opening)
                return;
            _state = ChannelState.Open;
            promoted = true;
        }
        if (promoted && !_isReady)
        {
            _isReady = true;
            // Raise synchronous Ready first so handlers observe a ready channel,
            // then complete the TCS so async awaiters resume only after handlers ran.
            // Multicast-safe: a throwing user handler must not crash the mux reader
            // thread that drove MarkOpen (#286).
            SafeEventRaiser.Raise(this, Ready, _onHandlerException);
            _readyTcs.TrySetResult();
        }
    }

    internal void MarkConnected()
    {
        _isConnected = true;
        SafeEventRaiser.Raise(this, Connected, _onHandlerException);
    }

    internal void MarkDisconnected(DisconnectReason reason, Exception? exception = null)
    {
        _isConnected = false;
        // The reconnect handshake advertises this side's current _frameBytesReceived
        // and the peer rewinds its writer to that exact position (see WriteChannel.SetReplayBase).
        // Consequently the very next byte the peer sends is the one we expect next, so we
        // never have to skip a replayed prefix. Computing skip from local-only state would
        // be wrong: _ackSentFrameBytes is bumped on local enqueue of an ACK, not on peer
        // delivery, so a lost ACK frame on the wire would yield a too-short skip and
        // duplicate-deliver bytes to ReadAsync (issue #161).
        _skipFrameBytes = 0;
        SafeEventRaiser.Raise(this, Disconnected, new DisconnectedEventArgs(reason, exception), _onHandlerException);
    }

    /// <summary>
    /// Total frame bytes (header + payload) received on this channel across all sessions,
    /// counting every frame type that consumes slab on the peer's writer — currently INIT
    /// and DATA. Advertised in the reconnect handshake so the peer's writer can rewind its
    /// replay base to exactly this position (issue #161).
    /// </summary>
    internal long FrameBytesReceived => _frameBytesReceived;

    /// <summary>
    /// Account for an inbound frame whose bytes were consumed off the wire for this
    /// channel but did not reach <see cref="ReceivePayload"/> — namely the initial
    /// <c>INIT</c> frame that the mux processes at channel registration time. The peer's
    /// writer slab includes those bytes, so omitting them here would make the reconnect
    /// handshake's replay-base land mid-frame and the writer would replay already-delivered
    /// bytes.
    /// </summary>
    internal void AccountInboundFrame(int frameBytes)
    {
        _frameBytesReceived += frameBytes;
    }

    internal void SetAckChannel(WriteChannel ackChannel) => _ackChannel = ackChannel;

    internal void SetChannelIndex(ushort index) => _channelIndex = index;

    /// <summary>
    /// Reads data from the channel. Fast path: data already buffered → immediate return.
    /// Slow path: registers for direct delivery from the mux reader thread.
    /// </summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return new ValueTask<int>(0);

        bool returnSlabAfter = false;
        try
        {
            lock (_lock)
            {
                if (_state == ChannelState.Closed && _receivedPos <= _consumedPos)
                {
                    returnSlabAfter = true;
                    return new ValueTask<int>(0); // EOF
                }

                // Fast path: data already buffered in slab
                int buffered = _receivedPos - _consumedPos;
                if (buffered > 0)
                {
                    int toCopy = Math.Min(buffered, buffer.Length);
                    _slabMemory.Span.Slice(_consumedPos, toCopy).CopyTo(buffer.Span);
                    _consumedPos += toCopy;
                    Interlocked.Add(ref Stats._bytesReceived, toCopy);
                    // If the consumer just drained the last buffered bytes
                    // after the channel closed, release the slab now.
                    if (_state == ChannelState.Closed && _receivedPos <= _consumedPos)
                        returnSlabAfter = true;
                    return new ValueTask<int>(toCopy);
                }

                if (_state == ChannelState.Closed)
                {
                    returnSlabAfter = true;
                    return new ValueTask<int>(0); // EOF
                }

            // Reader caught up to the writer with no buffered data: a natural
            // moment to flush any sub-threshold ACK so the writer's slab can
            // compact while we park, instead of pinning unacked bytes until
            // the next burst trips the receive-side threshold.
            MaybeSendAck();

            // Slow path: register for direct delivery
            _pendingUserBuffer = buffer;
            _readCompletion.Core.Reset();
            _readCompletionActive = true;

            // Handle cancellation
            if (ct.CanBeCanceled)
            {
                _readCancelReg = ct.Register(static state =>
                {
                    var self = (ReadChannel)state!;
                    lock (self._lock)
                    {
                        if (self._readCompletionActive)
                        {
                            self._pendingUserBuffer = null;
                            self._readCompletionActive = false;
                            self._readCancelReg = default;
                            self._readCompletion.Core.SetException(new OperationCanceledException());
                        }
                    }
                }, this);
            }

            return new ValueTask<int>(this, _readCompletion.Core.Version);
        }
        }
        finally
        {
            if (returnSlabAfter)
                TryReturnSlab();
        }
    }

    // Called by mux reader thread — the channel decides what to do with the payload
    internal void ReceivePayload(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        switch (flags)
        {
            case FrameFlags.Data:
                Interlocked.Increment(ref Stats._framesReceived);
                lock (_lock)
                {
                    if (_state == ChannelState.Closed) break;

                    int frameBytes = FrameHeader.Size + payload.Length;

                    // Skip whole frames that were already received before the
                    // last disconnect; the writer is replaying them now.
                    if (_skipFrameBytes > 0)
                    {
                        if (_skipFrameBytes >= frameBytes)
                        {
                            _skipFrameBytes -= frameBytes;
                            break;
                        }
                        // Misaligned skip should never happen with frame-granular ACKs.
                        throw new MultiplexerException(
                            ErrorCode.ProtocolError,
                            "Replay skip is not aligned to a frame boundary.");
                    }

                    _frameBytesReceived += frameBytes;
                    if (!TryDirectDeliver(payload))
                        BufferInSlab(payload);
                    MaybeSendAck();
                }
                break;

            case FrameFlags.Ack:
                if (payload.Length >= 8)
                {
                    long ackPos = (long)BinaryPrimitives.ReadUInt64BigEndian(payload);
                    _ackChannel?.OnAck(ackPos);
                }
                break;

            case FrameFlags.Fin:
                SetClosed(ChannelCloseReason.RemoteFin);
                break;

            case FrameFlags.Err:
                SetClosed(ChannelCloseReason.RemoteError);
                break;
        }
    }

    private bool TryDirectDeliver(ReadOnlySpan<byte> payload)
    {
        if (_pendingUserBuffer is { } userBuf && _readCompletionActive)
        {
            int toCopy = Math.Min(payload.Length, userBuf.Length);
            payload[..toCopy].CopyTo(userBuf.Span);
            _pendingUserBuffer = null;
            _readCompletionActive = false;
            Interlocked.Add(ref Stats._bytesReceived, toCopy);

            // Buffer remaining bytes BEFORE signaling completion.
            // SetResult may inline the continuation (lock is reentrant),
            // and the next ReadAsync must find remaining bytes in the slab.
            if (toCopy < payload.Length)
            {
                BufferInSlab(payload[toCopy..]);
            }

            // Detach the cancellation callback now that the read has
            // completed. Unregister is non-blocking so it is safe to call
            // under the lock that the callback would otherwise contend on.
            var reg = _readCancelReg;
            _readCancelReg = default;
            reg.Unregister();

            _readCompletion.Core.SetResult(toCopy);
            return true;
        }
        return false;
    }

    private void BufferInSlab(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return;

        // A single frame must fit in the receiver's slab. Sender must respect
        // receiver's slab capacity via flow-control ACKs. Anything else is a
        // protocol violation and silent truncation is not acceptable.
        if (payload.Length > _slabSize)
        {
            throw new MultiplexerException(
                ErrorCode.ProtocolError,
                $"Frame payload ({payload.Length} bytes) exceeds receiver slab capacity ({_slabSize} bytes).");
        }

        // Check if we have room, compact if needed
        int freeSpace = _slabSize - _receivedPos;
        if (freeSpace < payload.Length)
        {
            TryCompact();
            freeSpace = _slabSize - _receivedPos;
        }

        if (freeSpace < payload.Length)
        {
            // The sender exceeded the flow-control window. ACK-based
            // backpressure cannot push these bytes back to the wire, so
            // dropping them silently would corrupt the stream.
            throw new MultiplexerException(
                ErrorCode.ProtocolError,
                $"Frame payload ({payload.Length} bytes) exceeds available slab space ({freeSpace} bytes); sender violated flow control.");
        }

        payload.CopyTo(_slab.AsSpan(_receivedPos, payload.Length));
        _receivedPos += payload.Length;
    }

    private void TryCompact()
    {
        int consumed = _consumedPos;
        if (consumed <= 0) return;

        int remaining = _receivedPos - consumed;
        if (remaining > 0)
        {
            _slab.AsSpan(consumed, remaining).CopyTo(_slab.AsSpan(0, remaining));
        }
        _receivedPos = remaining;
        _consumedPos = 0;
    }

    // Send an ACK when the cumulative unacked frame bytes cross 1/16 of the
    // slab. The reported value is cumulative frame bytes received, which
    // matches the writer's slab position exactly so OnAck can apply it as a
    // monotonic position without any unit conversion. Called from the
    // receive path on every Data frame and from ReadAsync's slow path when
    // the reader catches up: same gate, no duplicate policy.
    private void MaybeSendAck()
    {
        if (_owner is null) return;
        long delta = _frameBytesReceived - _ackSentFrameBytes;
        if (delta >= _slabSize / 16)
        {
            _owner.SendAck(_channelIndex, (ulong)_frameBytesReceived);
            _ackSentFrameBytes = _frameBytesReceived;
        }
    }

    /// <summary>
    /// Gracefully close this read channel.
    /// </summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (_state is ChannelState.Closing or ChannelState.Closed)
            return ValueTask.CompletedTask;

        SetClosed(ChannelCloseReason.LocalClose);
        return ValueTask.CompletedTask;
    }

    internal void SetClosed(ChannelCloseReason reason, Exception? exception = null)
    {
        bool returnSlabNow = false;
        lock (_lock)
        {
            if (_state == ChannelState.Closed) return;
            _state = ChannelState.Closed;
            _closeReason = reason;
            _closeException = exception;
            _isConnected = false;

            // Wake anyone waiting for ready (channel will never open)
            _readyTcs.TrySetException(new ChannelClosedException(ChannelId, reason));

            // Wake any pending reader with EOF (0 bytes)
            if (_readCompletionActive)
            {
                _pendingUserBuffer = null;
                _readCompletionActive = false;
                var reg = _readCancelReg;
                _readCancelReg = default;
                reg.Unregister();
                _readCompletion.Core.SetResult(0);
            }

            // Slab return policy on close:
            // - MuxDisposed: the mux is gone, no further reads can succeed.
            //   Drop any buffered data (zero positions so subsequent ReadAsync
            //   returns EOF) and release the slab unconditionally. Mirrors
            //   WriteChannel.SetClosed's MuxDisposed branch (issue #169).
            // - Graceful close with no buffered data (Fin/Err/LocalClose):
            //   no consumer drain is possible; release the slab immediately.
            // - Graceful close with buffered data: preserve the slab so the
            //   consumer can drain to EOF; ReadAsync returns the slab when
            //   it observes EOF, and DisposeAsync catches any abandoned case.
            if (reason == ChannelCloseReason.MuxDisposed)
            {
                _receivedPos = 0;
                _consumedPos = 0;
                returnSlabNow = true;
            }
            else if (_receivedPos <= _consumedPos)
            {
                returnSlabNow = true;
            }
        }
        SafeEventRaiser.Raise(this, Closed, new ChannelCloseEventArgs(reason, exception), _onHandlerException);
        if (returnSlabNow)
            TryReturnSlab();
    }

    private void TryReturnSlab()
    {
        if (Interlocked.CompareExchange(ref _slabReturned, 1, 0) != 0) return;
        ArrayPool<byte>.Shared.Return(_slab);
    }

    // IValueTaskSource<int> implementation — used by the slow-path ReadAsync
    int IValueTaskSource<int>.GetResult(short token) => _readCompletion.Core.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _readCompletion.Core.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readCompletion.Core.OnCompleted(continuation, state, token, flags);

    // Stream plumbing
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_state is not ChannelState.Closed)
        {
            SetClosed(ChannelCloseReason.LocalClose);
        }
        TryReturnSlab();
        // A pending-accept channel disposed before the peer's INIT arrives
        // (_channelIndex == 0, never wired) must remove itself from the
        // pending-accept map so the dispatcher does not resurrect this
        // disposed instance when INIT eventually arrives.
        if (_channelIndex == 0)
            _owner?.NotifyPendingAcceptCancelled(ChannelId);
        _owner?.NotifyChannelCompleted(_channelIndex, ChannelId);
        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_state is not ChannelState.Closed)
            {
                SetClosed(ChannelCloseReason.LocalClose);
            }
            TryReturnSlab();
            if (_channelIndex == 0)
                _owner?.NotifyPendingAcceptCancelled(ChannelId);
            _owner?.NotifyChannelCompleted(_channelIndex, ChannelId);
        }
        base.Dispose(disposing);
    }

    // Wraps ManualResetValueTaskSourceCore to avoid CS1690 on MarshalByRefObject-derived classes
    private sealed class ValueTaskCompletionSource
    {
        public ManualResetValueTaskSourceCore<int> Core;
    }
}
