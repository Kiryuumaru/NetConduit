using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// A write-only channel for sending data. Opened by this side.
/// </summary>
public sealed class WriteChannel : Stream
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly ChannelOptions _options;
    private readonly SemaphoreSlim _creditSemaphore;
    private readonly SemaphoreSlim _writeLock;
    private readonly CancellationTokenSource _closeCts;
    private readonly ChannelSyncState _syncState;
    private readonly TaskCompletionSource _openTcs;
    
    // ARQ: Sequence tracking and resend buffer (byte-based, sized to MaxCredits)
    private readonly ConcurrentDictionary<uint, byte[]> _resendBuffer;
    private readonly long _maxResendBufferBytes;  // Derived from MaxCredits - guarantees all in-flight data can be retransmitted
    private long _resendBufferBytes;  // Current bytes in resend buffer
    private uint _nextSeq;
    private uint _ackedSeq; // Highest seq acknowledged
    
    private volatile ChannelState _state;
    private long _availableCredits;
    private bool _disposed;
    private ChannelCloseReason? _closeReason;
    private Exception? _closeException;

    internal WriteChannel(
        StreamMultiplexer multiplexer,
        uint channelIndex,
        string channelId,
        ChannelPriority priority,
        ChannelOptions options)
    {
        _multiplexer = multiplexer;
        ChannelIndex = channelIndex;
        ChannelId = channelId;
        Priority = priority;
        _options = options;
        _state = ChannelState.Opening;
        _availableCredits = 0;
        _creditSemaphore = new SemaphoreSlim(0, int.MaxValue);
        _writeLock = new SemaphoreSlim(1, 1);
        _closeCts = new CancellationTokenSource();
        _syncState = new ChannelSyncState(multiplexer.Options.ReconnectBufferSize);
        _openTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stats = new ChannelStats();
        
        // ARQ initialization - buffer sized to MaxCredits for guaranteed retransmit capability
        _resendBuffer = new ConcurrentDictionary<uint, byte[]>();
        _maxResendBufferBytes = options.MaxCredits;  // Auto-derived from channel's MaxCredits
        _resendBufferBytes = 0;
        _nextSeq = 0;
        _ackedSeq = 0;
    }

    /// <summary>The internal channel index used in wire protocol.</summary>
    internal uint ChannelIndex { get; }
    
    /// <summary>The string channel identifier.</summary>
    public string ChannelId { get; }
    
    /// <summary>Current channel state.</summary>
    public ChannelState State => _state;
    
    /// <summary>Channel priority.</summary>
    public ChannelPriority Priority { get; }
    
    /// <summary>Available send credits in bytes.</summary>
    public long AvailableCredits => Volatile.Read(ref _availableCredits);
    
    /// <summary>Channel statistics.</summary>
    public ChannelStats Stats { get; }
    
    /// <summary>Event raised when the channel is closed.</summary>
    public event Action<ChannelCloseReason, Exception?>? OnClosed;
    
    /// <summary>The reason for channel closure, if closed.</summary>
    public ChannelCloseReason? CloseReason => _closeReason;
    
    /// <summary>The exception associated with channel closure, if any.</summary>
    public Exception? CloseException => _closeException;
    
    /// <summary>Synchronization state for reconnection.</summary>
    internal ChannelSyncState SyncState => _syncState;

    /// <inheritdoc/>
    public override bool CanRead => false;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => _state == ChannelState.Open;
    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() { }
    
    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("WriteChannel does not support reading.");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Check close reason first - provide more meaningful exception
        if (_closeReason.HasValue)
            throw new ChannelClosedException(ChannelId, _closeReason.Value, _closeException);
        
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_state != ChannelState.Open)
        {
            throw new InvalidOperationException($"Channel is not open. State: {_state}");
        }

        // Handle zero-length writes - just return without doing anything
        if (buffer.Length == 0)
            return;

        // Acquire write lock to ensure this entire WriteAsync completes atomically
        // This prevents interleaving when multiple threads write to the same channel
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = buffer;
            while (remaining.Length > 0)
        {
            // Fast path: check if we already have credits without waiting
            var credits = Volatile.Read(ref _availableCredits);
            if (credits <= 0)
            {
                // Slow path: need to wait for credits
                // Only allocate CTS when actually needed (timeout case)
                if (_options.SendTimeout != Timeout.InfiniteTimeSpan)
                {
                    using var timeoutCts = new CancellationTokenSource(_options.SendTimeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _closeCts.Token);
                    try
                    {
                        await _creditSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Send operation timed out waiting for credits after {_options.SendTimeout}");
                    }
                }
                else
                {
                    // No timeout - just wait with user token and close token
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
                    await _creditSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                
                credits = Volatile.Read(ref _availableCredits);
                if (credits <= 0)
                    continue; // Spurious wakeup, retry
            }

            var toSend = (int)Math.Min(remaining.Length, Math.Min(credits, _multiplexer.Options.MaxFrameSize));
            
            // Consume credits atomically - use CompareExchange for lock-free
            long newCredits;
            long oldCredits;
            do
            {
                oldCredits = Volatile.Read(ref _availableCredits);
                if (oldCredits <= 0)
                {
                    toSend = 0;
                    break;
                }
                toSend = (int)Math.Min(remaining.Length, Math.Min(oldCredits, _multiplexer.Options.MaxFrameSize));
                newCredits = oldCredits - toSend;
            } while (Interlocked.CompareExchange(ref _availableCredits, newCredits, oldCredits) != oldCredits);
            
            if (toSend == 0)
                continue;
                
            Stats.AddCreditsConsumed(toSend);

            // Buffer for potential replay (if reconnection enabled) and send the data frame
            var slice = remaining[..toSend];
            
            // ARQ: Assign sequence number and store in resend buffer
            var seq = _nextSeq++;
            var frameData = slice.ToArray();
            
            // Store in resend buffer (may evict old entries if buffer full)
            StoreInResendBuffer(seq, frameData);
            
            if (_multiplexer.Options.EnableReconnection)
            {
                _syncState.RecordSend(frameData); // sync state needs owned copy
            }
            
            await _multiplexer.SendDataFrameAsync(ChannelIndex, seq, frameData, Priority, cancellationToken).ConfigureAwait(false);
            
            Stats.AddBytesSent(toSend);
            Stats.IncrementFramesSent();
            
                remaining = remaining[toSend..];

                // If we have more credits, release the semaphore for next iteration
                if (Volatile.Read(ref _availableCredits) > 0)
                    _creditSemaphore.Release();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gracefully close the channel.
    /// </summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ChannelState.Closed || _state == ChannelState.Closing)
            return;

        _state = ChannelState.Closing;
        await _multiplexer.SendFinAsync(ChannelIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the channel to be opened (ACK received from peer).
    /// </summary>
    internal Task WaitForOpenAsync(CancellationToken cancellationToken)
        => _openTcs.Task.WaitAsync(cancellationToken);

    internal void SetOpen(uint initialCredits)
    {
        _state = ChannelState.Open;
        GrantCredits(initialCredits);
        _openTcs.TrySetResult();
    }

    internal void GrantCredits(uint credits)
    {
        if (credits == 0) return;
        
        var previous = Interlocked.Add(ref _availableCredits, credits);
        Stats.AddCreditsGranted(credits);
        
        // Signal that credits are available
        if (previous - credits <= 0)
            _creditSemaphore.Release();
    }
    
    /// <summary>
    /// Handles acknowledgment sequence - frees all frames up to and including ackSeq.
    /// </summary>
    internal void HandleAckSeq(uint ackSeq)
    {
        // Free all frames with seq <= ackSeq
        foreach (var seq in _resendBuffer.Keys)
        {
            // Handle wraparound: if ackSeq wrapped, free both old high seqs and new low seqs
            // Simple approach: free if seq <= ackSeq (assuming no massive gaps)
            if (IsSeqAcked(seq, ackSeq))
            {
                if (_resendBuffer.TryRemove(seq, out var data))
                {
                    Interlocked.Add(ref _resendBufferBytes, -data.Length);
                }
            }
        }
        
        // Update acked seq
        if (IsSeqNewer(ackSeq, _ackedSeq))
        {
            _ackedSeq = ackSeq;
        }
    }
    
    /// <summary>
    /// Handles NACK by retransmitting the specified frame.
    /// </summary>
    internal async ValueTask HandleNackAsync(uint seq, CancellationToken ct)
    {
        if (_resendBuffer.TryGetValue(seq, out var frameData))
        {
            // Retransmit the frame with same seq
            await _multiplexer.SendDataFrameAsync(ChannelIndex, seq, frameData, Priority, ct).ConfigureAwait(false);
            Stats.IncrementRetransmissions();
        }
        // If frame not found, it was already acknowledged and removed - ignore
    }
    
    /// <summary>
    /// Gets the next sequence number (for control frames that need seq).
    /// </summary>
    internal uint GetNextSeq() => _nextSeq;
    
    private void StoreInResendBuffer(uint seq, byte[] data)
    {
        _resendBuffer[seq] = data;
        Interlocked.Add(ref _resendBufferBytes, data.Length);
        
        // No eviction needed - credit system guarantees we can't exceed MaxCredits bytes in-flight
        // The resend buffer is sized to MaxCredits, so all in-flight data is always retained
    }
    
    /// <summary>
    /// Returns true if seq is acknowledged by ackSeq (seq is less than or equal to ackSeq with wraparound).
    /// </summary>
    private static bool IsSeqAcked(uint seq, uint ackSeq)
    {
        // Simple comparison - handles wraparound if gap < 2^31
        return (int)(seq - ackSeq) <= 0;
    }
    
    /// <summary>
    /// Returns true if seqA is newer than seqB (accounting for wraparound).
    /// </summary>
    private static bool IsSeqNewer(uint seqA, uint seqB)
    {
        return (int)(seqA - seqB) > 0;
    }

    internal void SetClosed()
    {
        SetClosed(ChannelCloseReason.RemoteFin, null);
    }

    internal void SetClosed(ChannelCloseReason reason, Exception? exception)
    {
        if (_state == ChannelState.Closed)
            return;
            
        _closeReason = reason;
        _closeException = exception;
        _state = ChannelState.Closed;
        _closeCts.Cancel();
        _openTcs.TrySetCanceled();
        
        try
        {
            OnClosed?.Invoke(reason, exception);
        }
        catch
        {
            // Swallow exceptions from event handlers
        }
    }

    /// <summary>
    /// Aborts the channel immediately with the specified reason.
    /// </summary>
    internal void Abort(ChannelCloseReason reason, Exception? exception = null)
    {
        SetClosed(reason, exception);
    }

    internal void SetError(ErrorCode code, string message)
    {
        var exception = new MultiplexerException(code, message);
        _closeReason = ChannelCloseReason.RemoteError;
        _closeException = exception;
        _state = ChannelState.Closed;
        _closeCts.Cancel();
        _openTcs.TrySetException(exception);
        
        try
        {
            OnClosed?.Invoke(ChannelCloseReason.RemoteError, exception);
        }
        catch
        {
            // Swallow exceptions from event handlers
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _closeCts.Cancel();
            _creditSemaphore.Dispose();
            _writeLock.Dispose();
            _closeCts.Dispose();
            _multiplexer.OnWriteChannelDisposed(ChannelIndex, ChannelId);
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // If already aborted by mux/transport, skip graceful close
        if (_closeReason.HasValue && (_closeReason == ChannelCloseReason.MuxDisposed || _closeReason == ChannelCloseReason.TransportFailed))
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return;
        }

        if (_state == ChannelState.Open)
        {
            try
            {
                // Wait for pending writes to complete with timeout
                var timeout = _multiplexer.Options.GracefulShutdownTimeout;
                using var timeoutCts = new CancellationTokenSource(timeout);
                
                // Try to acquire write lock to ensure pending writes complete
                if (await _writeLock.WaitAsync(timeout).ConfigureAwait(false))
                {
                    _writeLock.Release();
                }
                
                await CloseAsync().ConfigureAwait(false);
                _closeReason = ChannelCloseReason.LocalClose;
                
                try
                {
                    OnClosed?.Invoke(ChannelCloseReason.LocalClose, null);
                }
                catch
                {
                    // Swallow exceptions from event handlers
                }
            }
            catch
            {
                // Ignore errors during dispose - set reason if not already set
                _closeReason ??= ChannelCloseReason.LocalClose;
            }
        }

        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
