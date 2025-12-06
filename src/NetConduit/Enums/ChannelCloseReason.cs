namespace NetConduit;

/// <summary>
/// Reason for channel closure.
/// </summary>
public enum ChannelCloseReason
{
    /// <summary>
    /// Local side closed the channel gracefully.
    /// </summary>
    LocalClose,

    /// <summary>
    /// Remote side sent FIN frame (graceful close).
    /// </summary>
    RemoteFin,

    /// <summary>
    /// Remote side sent ERR frame.
    /// </summary>
    RemoteError,

    /// <summary>
    /// Transport layer failed.
    /// </summary>
    TransportFailed,

    /// <summary>
    /// Parent multiplexer was disposed.
    /// </summary>
    MuxDisposed
}
