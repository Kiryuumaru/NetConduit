namespace NetConduit;

/// <summary>
/// Channel priority level for writer thread ordering.
/// </summary>
public enum ChannelPriority : byte
{
    /// <summary>Lowest priority (0).</summary>
    Lowest = 0,
    /// <summary>Low priority (64).</summary>
    Low = 64,
    /// <summary>Normal priority (128). Default.</summary>
    Normal = 128,
    /// <summary>High priority (192).</summary>
    High = 192,
    /// <summary>Highest priority (255).</summary>
    Highest = 255,
}
