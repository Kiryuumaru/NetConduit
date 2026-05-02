namespace NetConduit;

/// <summary>
/// Statistics for a single channel.
/// </summary>
public sealed class ChannelStats
{
    internal long _bytesSent;
    internal long _bytesReceived;
    internal long _framesSent;
    internal long _framesReceived;

    /// <summary>Total bytes sent on this channel.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>Total bytes received on this channel.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>Total frames sent on this channel.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);

    /// <summary>Total frames received on this channel.</summary>
    public long FramesReceived => Volatile.Read(ref _framesReceived);
}
