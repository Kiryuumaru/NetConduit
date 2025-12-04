namespace NetConduit;

/// <summary>
/// Constants for channel index allocation (internal wire protocol).
/// </summary>
public static class ChannelIndexLimits
{
    /// <summary>Control channel index (reserved for control messages).</summary>
    public const uint ControlChannel = 0x00000000;
    
    /// <summary>Minimum data channel index (inclusive).</summary>
    public const uint MinDataChannel = 0x00000001;
    
    /// <summary>Maximum data channel index (inclusive).</summary>
    public const uint MaxDataChannel = 0xFFFFFFFE;
    
    /// <summary>Reserved channel index (not used).</summary>
    public const uint Reserved = 0xFFFFFFFF;
}
