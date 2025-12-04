namespace NetConduit;

/// <summary>
/// Frame type flags. Not combinable - each frame has exactly one type.
/// </summary>
public enum FrameFlags : byte
{
    /// <summary>Regular data frame.</summary>
    Data = 0x00,
    
    /// <summary>Open channel (payload: priority u8).</summary>
    Init = 0x01,
    
    /// <summary>Close channel (graceful).</summary>
    Fin = 0x02,
    
    /// <summary>Credit grant (payload: credits u32).</summary>
    Ack = 0x03,
    
    /// <summary>Error (payload: code u16 + message UTF-8).</summary>
    Err = 0x04
}
