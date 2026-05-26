namespace NetConduit.Enums;

/// <summary>
/// Frame type flags in the wire protocol header.
/// </summary>
public enum FrameFlags : byte
{
    /// <summary>Regular data frame.</summary>
    Data = 0x00,
    /// <summary>Open channel (payload = channel name UTF-8).</summary>
    Init = 0x01,
    /// <summary>Close channel gracefully.</summary>
    Fin = 0x02,
    /// <summary>Acknowledge received bytes (payload = 8B big-endian UInt64 position).</summary>
    Ack = 0x03,
    /// <summary>Error (payload = error code + message).</summary>
    Err = 0x04,
    /// <summary>Keepalive request (payload = 8B timestamp).</summary>
    Ping = 0x05,
    /// <summary>Keepalive response.</summary>
    Pong = 0x06,
    /// <summary>Control subframe (reconnect, go-away, etc.).</summary>
    Ctrl = 0x07,
}
