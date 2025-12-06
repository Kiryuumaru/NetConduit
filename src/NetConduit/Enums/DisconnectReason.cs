namespace NetConduit;

/// <summary>
/// Reason for multiplexer disconnection.
/// </summary>
public enum DisconnectReason
{
    /// <summary>
    /// Received GOAWAY frame from remote side.
    /// </summary>
    GoAwayReceived,

    /// <summary>
    /// Transport layer error (stream closed, exception thrown).
    /// </summary>
    TransportError,

    /// <summary>
    /// Local side initiated dispose.
    /// </summary>
    LocalDispose
}
