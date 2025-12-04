namespace NetConduit;

/// <summary>
/// Multiplexer error codes.
/// </summary>
public enum ErrorCode : ushort
{
    /// <summary>No error.</summary>
    None = 0x0000,
    
    /// <summary>Channel ID not found.</summary>
    UnknownChannel = 0x0001,
    
    /// <summary>Channel ID already in use.</summary>
    ChannelExists = 0x0002,
    
    /// <summary>Malformed frame or invalid state.</summary>
    ProtocolError = 0x0003,
    
    /// <summary>Sent data exceeding credits.</summary>
    FlowControlError = 0x0004,
    
    /// <summary>Operation timed out.</summary>
    Timeout = 0x0005,
    
    /// <summary>Internal error.</summary>
    Internal = 0x0006,
    
    /// <summary>Channel open refused.</summary>
    Refused = 0x0007,
    
    /// <summary>Operation cancelled.</summary>
    Cancel = 0x0008,
    
    /// <summary>Session ID mismatch during reconnection.</summary>
    SessionMismatch = 0x0009
}
