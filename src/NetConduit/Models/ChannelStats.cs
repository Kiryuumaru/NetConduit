namespace NetConduit.Models;

/// <summary>
/// Statistics for a single channel.
/// </summary>
/// <remarks>
/// Byte counters here measure <b>payload bytes</b> only — frame headers and
/// per-frame overhead are <i>not</i> included. For wire-level accounting use
/// <see cref="MultiplexerStats.BytesSent"/> / <see cref="MultiplexerStats.BytesReceived"/>.
/// </remarks>
public sealed class ChannelStats
{
    internal long _bytesSent;
    internal long _bytesReceived;
    internal long _framesSent;
    internal long _framesReceived;

    /// <summary>Total payload bytes sent on this channel (no framing overhead).</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>Total payload bytes received on this channel (no framing overhead).</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>Total frames sent on this channel.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);

    /// <summary>Total frames received on this channel.</summary>
    public long FramesReceived => Volatile.Read(ref _framesReceived);
}
