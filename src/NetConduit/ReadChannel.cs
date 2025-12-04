using System.Threading.Channels;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// A read-only channel for receiving data. Accepted from remote side.
/// </summary>
public sealed class ReadChannel : Stream
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly ChannelOptions _options;
    private readonly Channel<OwnedMemory> _dataChannel;
    private readonly CancellationTokenSource _closeCts;
    private readonly ChannelSyncState _syncState;
    private readonly object _disposeLock = new();
    
    private volatile ChannelState _state;
    private OwnedMemory _currentOwnedBuffer;
    private ReadOnlyMemory<byte> _currentRemainingData;
    private long _consumedSinceLastGrant;
    private bool _disposed;
    private volatile bool _isDisposing;

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
        _dataChannel = Channel.CreateUnbounded<OwnedMemory>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _closeCts = new CancellationTokenSource();
        _syncState = new ChannelSyncState(0); // Read channels don't buffer, just track sequence
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

    /// <inheritdoc/>
    public override bool CanRead => _state == ChannelState.Open || !_currentRemainingData.IsEmpty || _dataChannel.Reader.TryPeek(out _);
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

        // If we have leftover data from previous read, use it first
        if (!_currentRemainingData.IsEmpty)
        {
            return ConsumeBuffer(buffer);
        }

        // Try to read any remaining data first, even if channel is closing
        if (_dataChannel.Reader.TryRead(out var data))
        {
            SetCurrentBuffer(data);
            Stats.IncrementFramesReceived();
            return ConsumeBuffer(buffer);
        }

        // If channel is closed and no more data, return 0
        if (_state == ChannelState.Closed && !_dataChannel.Reader.TryPeek(out _))
        {
            return 0;
        }

        // Wait for more data
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);
        
        try
        {
            if (!await _dataChannel.Reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                // Channel completed (closed)
                return 0;
            }

            if (_dataChannel.Reader.TryRead(out data))
            {
                SetCurrentBuffer(data);
                Stats.IncrementFramesReceived();
                return ConsumeBuffer(buffer);
            }
        }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        {
            // Channel closed - but try to drain any remaining data first
            if (_dataChannel.Reader.TryRead(out data))
            {
                SetCurrentBuffer(data);
                Stats.IncrementFramesReceived();
                return ConsumeBuffer(buffer);
            }
            return 0; // No more data
        }

        return 0;
    }

    private void SetCurrentBuffer(OwnedMemory owned)
    {
        // Dispose any previous buffer that wasn't fully consumed
        if (!_currentOwnedBuffer.IsDisposed && !_currentRemainingData.IsEmpty)
        {
            _currentOwnedBuffer.Dispose();
        }
        
        _currentOwnedBuffer = owned;
        _currentRemainingData = owned.ReadOnlyMemory;
    }

    private int ConsumeBuffer(Memory<byte> destination)
    {
        var toCopy = Math.Min(destination.Length, _currentRemainingData.Length);
        _currentRemainingData[..toCopy].CopyTo(destination);
        _currentRemainingData = _currentRemainingData[toCopy..];
        
        // If buffer is fully consumed, dispose it and return to pool
        if (_currentRemainingData.IsEmpty && !_currentOwnedBuffer.IsDisposed)
        {
            _currentOwnedBuffer.Dispose();
            _currentOwnedBuffer = default;
        }
        
        Stats.AddBytesReceived(toCopy);
        
        // Track consumed bytes for credit auto-grant
        _consumedSinceLastGrant += toCopy;
        var threshold = (long)(_options.InitialCredits * _options.CreditGrantThreshold);
        
        if (_consumedSinceLastGrant >= threshold)
        {
            var toGrant = (uint)_consumedSinceLastGrant;
            _consumedSinceLastGrant = 0;
            
            // Fire and forget credit grant
            _ = _multiplexer.SendCreditGrantAsync(ChannelIndex, toGrant, CancellationToken.None);
            Stats.AddCreditsGranted(toGrant);
        }

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
        
        // Grant any remaining credits
        if (_consumedSinceLastGrant > 0)
        {
            await _multiplexer.SendCreditGrantAsync(ChannelIndex, (uint)_consumedSinceLastGrant, cancellationToken).ConfigureAwait(false);
            _consumedSinceLastGrant = 0;
        }
    }

    internal void EnqueueData(OwnedMemory data)
    {
        // Use lock to synchronize with Dispose
        lock (_disposeLock)
        {
            if (_state == ChannelState.Closed || _isDisposing)
            {
                // Channel is closed/disposing - dispose the data immediately
                data.Dispose();
                return;
            }
            
            // Track bytes received for reconnection sync
            _syncState.RecordReceive(data.Length);
            
            if (!_dataChannel.Writer.TryWrite(data))
            {
                // If we can't write (shouldn't happen with unbounded), dispose
                data.Dispose();
            }
        }
    }

    internal void SetClosed()
    {
        _state = ChannelState.Closed;
        _dataChannel.Writer.TryComplete();
        _closeCts.Cancel();
    }

    internal void SetError(ErrorCode code, string message)
    {
        _state = ChannelState.Closed;
        _dataChannel.Writer.TryComplete(new MultiplexerException(code, message));
        _closeCts.Cancel();
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
                _dataChannel.Writer.TryComplete();
                
                // Dispose the current buffer if not already disposed
                if (!_currentOwnedBuffer.IsDisposed)
                {
                    _currentOwnedBuffer.Dispose();
                    _currentOwnedBuffer = default;
                    _currentRemainingData = default;
                }
                
                // Drain and dispose any remaining queued buffers
                while (_dataChannel.Reader.TryRead(out var remainingData))
                {
                    remainingData.Dispose();
                }
                
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
