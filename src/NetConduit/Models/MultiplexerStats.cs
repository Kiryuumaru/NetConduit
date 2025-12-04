namespace NetConduit;

/// <summary>
/// Statistics for the multiplexer.
/// </summary>
public sealed class MultiplexerStats
{
    private long _bytesSent;
    private long _bytesReceived;
    private int _openChannels;
    private int _totalChannelsOpened;
    private int _totalChannelsClosed;
    private long _lastPingRttTicks;
    private int _missedPings;
    private readonly DateTime _startedAt;

    internal MultiplexerStats()
    {
        _startedAt = DateTime.UtcNow;
    }

    /// <summary>Total bytes sent across all channels.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);
    
    /// <summary>Total bytes received across all channels.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);
    
    /// <summary>Currently open channels.</summary>
    public int OpenChannels => Volatile.Read(ref _openChannels);
    
    /// <summary>Total channels opened since start.</summary>
    public int TotalChannelsOpened => Volatile.Read(ref _totalChannelsOpened);
    
    /// <summary>Total channels closed since start.</summary>
    public int TotalChannelsClosed => Volatile.Read(ref _totalChannelsClosed);
    
    /// <summary>How long the multiplexer has been running.</summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedAt;
    
    /// <summary>Round-trip time of the last successful ping.</summary>
    public TimeSpan LastPingRtt => TimeSpan.FromTicks(Volatile.Read(ref _lastPingRttTicks));
    
    /// <summary>Number of consecutive missed pings.</summary>
    public int MissedPings => Volatile.Read(ref _missedPings);

    internal void AddBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
    internal void AddBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);
    internal void IncrementOpenChannels() => Interlocked.Increment(ref _openChannels);
    internal void DecrementOpenChannels() => Interlocked.Decrement(ref _openChannels);
    internal void IncrementTotalChannelsOpened() => Interlocked.Increment(ref _totalChannelsOpened);
    internal void IncrementTotalChannelsClosed() => Interlocked.Increment(ref _totalChannelsClosed);
    internal void SetLastPingRtt(TimeSpan rtt) => Volatile.Write(ref _lastPingRttTicks, rtt.Ticks);
    internal void SetMissedPings(int count) => Volatile.Write(ref _missedPings, count);
    internal void IncrementMissedPings() => Interlocked.Increment(ref _missedPings);
    internal void ResetMissedPings() => Volatile.Write(ref _missedPings, 0);
}
