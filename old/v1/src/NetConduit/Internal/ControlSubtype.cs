namespace NetConduit.Internal;

/// <summary>
/// Control channel subtypes.
/// </summary>
internal enum ControlSubtype : byte
{
    /// <summary>Ping request with timestamp.</summary>
    Ping = 0x01,
    
    /// <summary>Pong response echoing timestamp.</summary>
    Pong = 0x02,
    
    /// <summary>Handshake for session identification.</summary>
    Handshake = 0x03,
    
    /// <summary>Credit grant for flow control.</summary>
    CreditGrant = 0x04,
    
    /// <summary>Error notification.</summary>
    Error = 0x05,
    
    /// <summary>Graceful shutdown notification.</summary>
    GoAway = 0x06,
    
    /// <summary>Reconnection request with session_id and channel states.</summary>
    Reconnect = 0x07,
    
    /// <summary>Reconnection acknowledgment with channel state sync.</summary>
    ReconnectAck = 0x08
}
