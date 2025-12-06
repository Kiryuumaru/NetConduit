using System.Buffers;
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
    
    private volatile ChannelState _state;
    private long _availableCredits;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_state != ChannelState.Open)
            throw new InvalidOperationException($"Channel is not open. State: {_state}");

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
            if (_multiplexer.Options.EnableReconnection)
            {
                // Rent buffer, copy, record for replay, then send
                var rented = ArrayPool<byte>.Shared.Rent(toSend);
                try
                {
                    slice.CopyTo(rented);
                    _syncState.RecordSend(rented.AsMemory(0, toSend).ToArray()); // sync state needs owned copy
                    await _multiplexer.SendDataFrameAsync(ChannelIndex, rented.AsMemory(0, toSend), Priority, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            else
            {
                // No reconnection - pass memory directly without copying
                await _multiplexer.SendDataFrameAsync(ChannelIndex, slice, Priority, cancellationToken).ConfigureAwait(false);
            }
            
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

    internal void SetClosed()
    {
        _state = ChannelState.Closed;
        _closeCts.Cancel();
        _openTcs.TrySetCanceled();
    }

    internal void SetError(ErrorCode code, string message)
    {
        _state = ChannelState.Closed;
        _closeCts.Cancel();
        _openTcs.TrySetException(new MultiplexerException(code, message));
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

        if (_state == ChannelState.Open)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors during dispose
            }
        }

        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
