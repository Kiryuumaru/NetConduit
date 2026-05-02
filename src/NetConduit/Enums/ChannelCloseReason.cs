namespace NetConduit;

/// <summary>
/// Reason a channel was closed.
/// </summary>
public enum ChannelCloseReason
{
    /// <summary>Closed by the local side.</summary>
    LocalClose,
    /// <summary>Remote sent a FIN frame.</summary>
    RemoteFin,
    /// <summary>Remote sent an ERR frame.</summary>
    RemoteError,
    /// <summary>Underlying transport connection failed.</summary>
    TransportFailed,
    /// <summary>Parent multiplexer was disposed.</summary>
    MuxDisposed,
}
