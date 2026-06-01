namespace NetConduit.Models;

/// <summary>
/// Statistics for a multiplexer session.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="BytesSent"/> and <see cref="BytesReceived"/> counters here
/// measure <b>wire bytes</b> (frame headers and control-frame traffic
/// included). For payload-only accounting per channel, read
/// <see cref="ChannelStats.BytesSent"/> / <see cref="ChannelStats.BytesReceived"/>.
/// </para>
/// <para>
/// Reads are individually atomic (each property returns a volatile read),
/// but there is no cross-counter snapshot: a single <c>mux.Stats</c> access
/// followed by multiple property reads can observe each counter at a
/// different instant.
/// </para>
/// </remarks>
public sealed class MultiplexerStats
{
    internal long _bytesSent;
    internal long _bytesReceived;
    internal int _openChannels;
    internal int _totalChannelsOpened;
    internal int _totalChannelsClosed;
    internal long _startTicks;

    /// <summary>
    /// Total wire bytes written by the multiplexer's writer loop, including
    /// 12-byte frame headers and control-frame traffic (Ping/Pong, GoAway,
    /// INIT, FIN, etc.). This is <b>not</b> the same quantity as the sum of
    /// per-channel <see cref="ChannelStats.BytesSent"/>, which counts only
    /// user payload bytes.
    /// </summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>
    /// Total wire bytes read from the transport, including 12-byte frame
    /// headers and control-frame traffic. This is <b>not</b> the same
    /// quantity as the sum of per-channel
    /// <see cref="ChannelStats.BytesReceived"/>, which counts only user
    /// payload bytes.
    /// </summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>Currently open channels.</summary>
    public int OpenChannels => Volatile.Read(ref _openChannels);

    /// <summary>Total channels opened during this session.</summary>
    public int TotalChannelsOpened => Volatile.Read(ref _totalChannelsOpened);

    /// <summary>Total channels closed during this session.</summary>
    public int TotalChannelsClosed => Volatile.Read(ref _totalChannelsClosed);

    /// <summary>
    /// Elapsed wall time since <see cref="Interfaces.IStreamMultiplexer.Start"/>
    /// was called. Returns <see cref="TimeSpan.Zero"/> before start. The clock
    /// keeps advancing across reconnects (it is not paused while disconnected).
    /// </summary>
    public TimeSpan Uptime => _startTicks > 0
        ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _startTicks)
        : TimeSpan.Zero;
}
