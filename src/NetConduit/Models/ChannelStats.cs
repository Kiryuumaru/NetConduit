namespace NetConduit;

/// <summary>
/// Statistics for a single channel.
/// </summary>
public sealed class ChannelStats
{
    private long _bytesSent;
    private long _bytesReceived;
    private long _framesSent;
    private long _framesReceived;
    private long _creditsGranted;
    private long _creditsConsumed;
    private long _retransmissions;
    private long _crcFailures;
    private readonly DateTime _openedAt;

    internal ChannelStats()
    {
        _openedAt = DateTime.UtcNow;
    }

    /// <summary>Total bytes sent on this channel.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);
    
    /// <summary>Total bytes received on this channel.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);
    
    /// <summary>Total frames sent on this channel.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);
    
    /// <summary>Total frames received on this channel.</summary>
    public long FramesReceived => Volatile.Read(ref _framesReceived);
    
    /// <summary>Total credits granted by this side.</summary>
    public long CreditsGranted => Volatile.Read(ref _creditsGranted);
    
    /// <summary>Total credits consumed by this side.</summary>
    public long CreditsConsumed => Volatile.Read(ref _creditsConsumed);
    
    /// <summary>Total frames retransmitted due to NACK.</summary>
    public long Retransmissions => Volatile.Read(ref _retransmissions);
    
    /// <summary>Total frames received with CRC failures.</summary>
    public long CrcFailures => Volatile.Read(ref _crcFailures);
    
    /// <summary>How long the channel has been open.</summary>
    public TimeSpan OpenDuration => DateTime.UtcNow - _openedAt;

    internal void AddBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
    internal void AddBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);
    internal void IncrementFramesSent() => Interlocked.Increment(ref _framesSent);
    internal void IncrementFramesReceived() => Interlocked.Increment(ref _framesReceived);
    internal void AddCreditsGranted(long credits) => Interlocked.Add(ref _creditsGranted, credits);
    internal void AddCreditsConsumed(long credits) => Interlocked.Add(ref _creditsConsumed, credits);
    internal void IncrementRetransmissions() => Interlocked.Increment(ref _retransmissions);
    internal void IncrementCrcFailures() => Interlocked.Increment(ref _crcFailures);
}
