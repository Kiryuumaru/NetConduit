namespace NetConduit;

/// <summary>
/// Reason the multiplexer was disconnected.
/// </summary>
public enum DisconnectReason
{
    /// <summary>Remote peer initiated graceful shutdown.</summary>
    GoAwayReceived,
    /// <summary>Transport stream encountered an error.</summary>
    TransportError,
    /// <summary>Local side disposed the multiplexer.</summary>
    LocalDispose,
}
