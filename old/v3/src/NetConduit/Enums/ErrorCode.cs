namespace NetConduit.Enums;

/// <summary>
/// Protocol-level error codes transmitted in ERR frames.
/// </summary>
public enum ErrorCode : ushort
{
    /// <summary>No error.</summary>
    None = 0x0000,
    /// <summary>Frame references a channel index that does not exist.</summary>
    UnknownChannel = 0x0001,
    /// <summary>INIT for a channel index that is already in use.</summary>
    ChannelExists = 0x0002,
    /// <summary>Wire protocol violation (bad header, unexpected frame type).</summary>
    ProtocolError = 0x0003,
    /// <summary>Flow control window exceeded.</summary>
    FlowControlError = 0x0004,
    /// <summary>Operation timed out.</summary>
    Timeout = 0x0005,
    /// <summary>Internal multiplexer error.</summary>
    Internal = 0x0006,
    /// <summary>Channel open request was refused by the remote peer.</summary>
    Refused = 0x0007,
    /// <summary>Operation was cancelled.</summary>
    Cancel = 0x0008,
    /// <summary>Session ID mismatch during reconnection handshake.</summary>
    SessionMismatch = 0x0009,
}
