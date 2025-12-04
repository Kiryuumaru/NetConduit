namespace NetConduit;

/// <summary>
/// Configuration options for a channel.
/// </summary>
public sealed class ChannelOptions
{
    /// <summary>
    /// The unique string identifier for this channel. Required.
    /// Maximum 1024 bytes when UTF-8 encoded.
    /// </summary>
    public required string ChannelId { get; init; }
    
    /// <summary>
    /// Initial credits (bytes) granted to sender. Default: 1MB.
    /// </summary>
    public uint InitialCredits { get; init; } = 1024 * 1024;
    
    /// <summary>
    /// Threshold (0.0 to 1.0) of consumed credits before auto-granting more.
    /// Default: 0.5 (grant when 50% consumed).
    /// </summary>
    public double CreditGrantThreshold { get; init; } = 0.5;
    
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
