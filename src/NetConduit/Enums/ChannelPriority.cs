namespace NetConduit;

/// <summary>
/// Channel priority. Higher value = higher priority.
/// </summary>
public enum ChannelPriority : byte
{
    /// <summary>Background, best-effort (0).</summary>
    Lowest = 0,
    
    /// <summary>Bulk transfers (64).</summary>
    Low = 64,
    
    /// <summary>Default priority (128).</summary>
    Normal = 128,
    
    /// <summary>Interactive, low-latency (192).</summary>
    High = 192,
    
    /// <summary>Control messages, signaling (255).</summary>
    Highest = 255
}
