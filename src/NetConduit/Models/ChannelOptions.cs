using NetConduit.Constants;

namespace NetConduit;

/// <summary>
/// Options for opening a channel.
/// </summary>
public sealed class ChannelOptions
{
    /// <summary>String identifier for this channel.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Priority level for the writer thread.</summary>
    public ChannelPriority Priority { get; init; } = ChannelPriority.Normal;

    /// <summary>Slab size in bytes for this channel.</summary>
    public int SlabSize { get; init; } = FrameConstants.DefaultSlabSize;

    /// <summary>Timeout for write operations waiting for slab space.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Default channel options applied when channel-specific options are not provided.
/// </summary>
public sealed class DefaultChannelOptions
{
    /// <summary>Default priority level.</summary>
    public ChannelPriority Priority { get; init; } = ChannelPriority.Normal;

    /// <summary>Default slab size per channel.</summary>
    public int SlabSize { get; init; } = FrameConstants.DefaultSlabSize;

    /// <summary>Default send timeout.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
