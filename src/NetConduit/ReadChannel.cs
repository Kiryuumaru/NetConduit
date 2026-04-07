using System.Buffers;
using System.IO.Pipelines;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// A read-only channel for receiving data. Accepted from remote side.
/// </summary>
public sealed class ReadChannel : Stream
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly ChannelOptions _options;
    private readonly Pipe _dataPipe;
    private readonly PipeReader _dataPipeReader;
    private readonly PipeWriter _dataPipeWriter;
    private readonly CancellationTokenSource _closeCts;
    private readonly ChannelSyncState _syncState;
    private readonly AdaptiveFlowControl _flowControl;
    private readonly object _disposeLock = new();
    
    private volatile ChannelState _state;
    private ReadOnlySequence<byte> _bufferedData;
    private bool _hasBufferedData;
    private bool _disposed;
    private volatile bool _isDisposing;
    private ChannelCloseReason? _closeReason;
    private Exception? _closeException;
    private long _pendingGrantCredits;

    internal ReadChannel(
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
        _state = ChannelState.Open;
        _dataPipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false));
        _dataPipeReader = _dataPipe.Reader;
        _dataPipeWriter = _dataPipe.Writer;
        _closeCts = new CancellationTokenSource();
        _syncState = new ChannelSyncState(0); // Read channels don't buffer, just track sequence
        _flowControl = new AdaptiveFlowControl(options.MinCredits, options.MaxCredits);
        Stats = new ChannelStats();
        _isDisposing = false;
    }

    /// <summary>The internal channel index used in wire protocol.</summary>
    internal uint ChannelIndex { get; }
    
    /// <summary>The string channel identifier.</summary>
    public string ChannelId { get; }
    
    /// <summary>Current channel state.</summary>
    public ChannelState State => _state;
    
    /// <summary>Channel priority.</summary>
    public ChannelPriority Priority { get; }
    
    /// <summary>Channel statistics.</summary>
    public ChannelStats Stats { get; }
    
    /// <summary>Synchronization state for reconnection.</summary>
    internal ChannelSyncState SyncState => _syncState;
    
    /// <summary>Gets the current adaptive window size for flow control.</summary>
    public uint CurrentWindowSize => _flowControl.CurrentWindowSize;
    
    /// <summary>Gets the initial credits to grant for this channel.</summary>
    internal uint GetInitialCredits() => _flowControl.GetInitialCredits();
    
    internal long PendingGrantCredits => Volatile.Read(ref _pendingGrantCredits);
    
    internal uint DrainPendingCredits()
    {
        var credits = Interlocked.Exchange(ref _pendingGrantCredits, 0);
        return credits > 0 ? (uint)credits : 0;
    }
    
    /// <summary>Event raised when the channel is closed.</summary>
    public event Action<ChannelCloseReason, Exception?>? OnClosed;
    
    /// <summary>The reason for channel closure, if closed.</summary>
    public ChannelCloseReason? CloseReason => _closeReason;
    
    /// <summary>The exception associated with channel closure, if any.</summary>
    public Exception? CloseException => _closeException;

    /// <inheritdoc/>
    public override bool CanRead => _state == ChannelState.Open || _hasBufferedData;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => false;
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
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return 0;

        if (HotPathProfiler.IsEnabled) HotPathProfiler.RecordReadAsync();

        // If we have leftover data from previous read, use it first
        if (_hasBufferedData)
        {
            if (HotPathProfiler.IsEnabled) HotPathProfiler.RecordReadAsyncFastPath();
            return ConsumeBuffer(buffer);
        }

        // Try non-blocking read from the pipe
        if (_dataPipeReader.TryRead(out var readResult))
        {
            if (readResult.Buffer.Length > 0)
            {
                if (HotPathProfiler.IsEnabled) HotPathProfiler.RecordReadAsyncFastPath();
                _bufferedData = readResult.Buffer;
                _hasBufferedData = true;
                Stats.IncrementFramesReceived();
                return ConsumeBuffer(buffer);
            }
            _dataPipeReader.AdvanceTo(readResult.Buffer.Start);
            if (readResult.IsCompleted)
                return 0;
        }

        // If channel is closed and no more data, return 0
        if (_state == ChannelState.Closed)
        {
            return 0;
        }

        if (HotPathProfiler.IsEnabled) HotPathProfiler.RecordReadAsyncSlowPath();
        // Wait for more data
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        
        try
        {
            var result = await _dataPipeReader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
            
            if (result.Buffer.Length > 0)
            {
                _bufferedData = result.Buffer;
                _hasBufferedData = true;
                Stats.IncrementFramesReceived();
                return ConsumeBuffer(buffer);
            }
            
            _dataPipeReader.AdvanceTo(result.Buffer.Start);
            if (result.IsCompleted)
                return 0;
        }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        {
            // Channel closed - try to drain any remaining data
            if (_dataPipeReader.TryRead(out var finalResult) && finalResult.Buffer.Length > 0)
            {
                _bufferedData = finalResult.Buffer;
                _hasBufferedData = true;
                Stats.IncrementFramesReceived();
                return ConsumeBuffer(buffer);
            }
            if (finalResult.Buffer.Length == 0)
                _dataPipeReader.AdvanceTo(finalResult.Buffer.Start);
            return 0;
        }

        return 0;
    }

    private void SetCurrentBuffer(ReadOnlySequence<byte> data)
    {
        _bufferedData = data;
        _hasBufferedData = true;
    }

    private int ConsumeBuffer(Memory<byte> destination)
    {
        var profiling = HotPathProfiler.IsEnabled;
        long cbStart = profiling ? HotPathProfiler.Timestamp() : 0;
        long t0;
        
        var toCopy = (int)Math.Min(destination.Length, _bufferedData.Length);
        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        _bufferedData.Slice(0, toCopy).CopyTo(destination.Span);
        if (profiling) HotPathProfiler.RecordConsumeBufferCopy(HotPathProfiler.Timestamp() - t0);
        
        var consumed = _bufferedData.GetPosition(toCopy);
        var remaining = _bufferedData.Slice(toCopy);
        
        // If buffer is fully consumed, advance the PipeReader
        if (remaining.Length == 0)
        {
            t0 = profiling ? HotPathProfiler.Timestamp() : 0;
            _dataPipeReader.AdvanceTo(consumed);
            _hasBufferedData = false;
            _bufferedData = default;
            if (profiling)
            {
                HotPathProfiler.RecordConsumeBufferDispose(HotPathProfiler.Timestamp() - t0);
                HotPathProfiler.RecordReturn();
            }
        }
        else
        {
            // Partial consume: tell PipeReader what was examined but not consumed
            _bufferedData = remaining;
        }
        
        Stats.AddBytesReceived(toCopy);
        
        // Use adaptive flow control to determine credit grant
        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        var toGrant = _flowControl.RecordConsumptionAndGetGrant(toCopy);
        if (toGrant > 0)
        {
            if (profiling) HotPathProfiler.RecordCreditGrant(HotPathProfiler.Timestamp() - t0);
            Stats.AddCreditsGranted(toGrant);
            
            if (_multiplexer.Options.FlushMode == FlushMode.Batched)
            {
                // Accumulate — FlushLoopAsync drains pending queue under one lock
                Interlocked.Add(ref _pendingGrantCredits, toGrant);
                _multiplexer.EnqueuePendingCredit(this);
                _multiplexer.SignalFlush();
            }
            else
            {
                // Non-batched modes: send immediately (no flush loop running)
                _multiplexer.SendCreditGrant(ChannelIndex, toGrant, CancellationToken.None);
            }
        }
        else if (profiling)
        {
            HotPathProfiler.RecordCreditGrant(HotPathProfiler.Timestamp() - t0);
        }

        if (profiling) HotPathProfiler.RecordConsumeBuffer(HotPathProfiler.Timestamp() - cbStart);
        return toCopy;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("ReadChannel does not support writing.");

    /// <summary>
    /// Gracefully close the channel.
    /// </summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ChannelState.Closed || _state == ChannelState.Closing)
            return;

        _state = ChannelState.Closing;
        
        // Flush any remaining credits from adaptive flow control
        // The flow control handles this internally, nothing more to do here
    }

    internal void EnqueueData(ReadOnlySequence<byte> payload)
    {
        var profiling = HotPathProfiler.IsEnabled;
        long t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        // Use lock to synchronize with Dispose
        lock (_disposeLock)
        {
            if (profiling) HotPathProfiler.RecordEnqueueDataLock(HotPathProfiler.Timestamp() - t0);
            if (_state == ChannelState.Closed || _isDisposing)
            {
                return;
            }
            
            // Track bytes received for reconnection sync
            _syncState.RecordReceive((int)payload.Length);
            
            // Copy payload into per-channel PipeWriter
            var span = _dataPipeWriter.GetSpan((int)payload.Length);
            payload.CopyTo(span);
            _dataPipeWriter.Advance((int)payload.Length);
            _dataPipeWriter.FlushAsync().GetAwaiter().GetResult();
        }
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
        _dataPipeWriter.Complete();
        _closeCts.Cancel();
        
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
        _dataPipeWriter.Complete(exception);
        _closeCts.Cancel();
        
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
        
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isDisposing = true;

            if (disposing)
            {
                _closeCts.Cancel();
                _dataPipeWriter.Complete();
                _dataPipeReader.Complete();
                
                _hasBufferedData = false;
                _bufferedData = default;
                
                _closeCts.Dispose();
                _multiplexer.OnReadChannelDisposed(ChannelIndex, ChannelId);
            }
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
                // Wait for buffered data to be drained with timeout
                var timeout = _multiplexer.Options.GracefulShutdownTimeout;
                using var timeoutCts = new CancellationTokenSource(timeout);
                
                // Wait until buffer is drained or timeout
                while (_hasBufferedData)
                {
                    if (timeoutCts.IsCancellationRequested)
                        break;
                    await Task.Delay(10, timeoutCts.Token).ConfigureAwait(false);
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
