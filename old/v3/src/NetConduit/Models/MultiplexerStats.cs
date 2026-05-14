namespace NetConduit.Models;

/// <summary>
/// Statistics for a multiplexer session.
/// </summary>
public sealed class MultiplexerStats
{
    internal long _bytesSent;
    internal long _bytesReceived;
    internal int _openChannels;
    internal int _totalChannelsOpened;
    internal int _totalChannelsClosed;
    internal long _startTicks;

    /// <summary>Total bytes sent across all channels.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>Total bytes received across all channels.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>Currently open channels.</summary>
    public int OpenChannels => Volatile.Read(ref _openChannels);

    /// <summary>Total channels opened during this session.</summary>
    public int TotalChannelsOpened => Volatile.Read(ref _totalChannelsOpened);

    /// <summary>Total channels closed during this session.</summary>
    public int TotalChannelsClosed => Volatile.Read(ref _totalChannelsClosed);

    /// <summary>Time since the multiplexer started.</summary>
    public TimeSpan Uptime => _startTicks > 0
        ? TimeSpan.FromTicks(Environment.TickCount64 - _startTicks)
        : TimeSpan.Zero;
}
