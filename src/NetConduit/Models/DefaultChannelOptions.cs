namespace NetConduit;

/// <summary>
/// Default configuration options for channels (without ChannelId).
/// Used in MultiplexerOptions for setting default values.
/// </summary>
public sealed class DefaultChannelOptions
{
    /// <summary>
    /// Minimum credits (bytes) for adaptive flow control. Default: 64KB.
    /// Credits will not shrink below this value.
    /// </summary>
    public uint MinCredits { get; init; } = 64 * 1024;
    
    /// <summary>
    /// Maximum credits (bytes) for adaptive flow control. Default: 4MB.
    /// Credits will not grow beyond this value.
    /// </summary>
    public uint MaxCredits { get; init; } = 4 * 1024 * 1024;
    
    /// <summary>
    /// Timeout for send operations when waiting for credits.
    /// Default: 30 seconds. Use Timeout.InfiniteTimeSpan to wait forever.
    /// </summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Channel priority. Higher value = higher priority. Default: Normal (128).
    /// </summary>
    public ChannelPriority Priority { get; init; } = ChannelPriority.Normal;
}
