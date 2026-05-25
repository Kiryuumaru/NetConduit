using NetConduit.Constants;
using NetConduit.Enums;

namespace NetConduit.Models;

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

    /// <summary>
    /// Validates that all fields hold legal values. Called by
    /// <see cref="NetConduit.Interfaces.IStreamMultiplexer.OpenChannel(ChannelOptions)"/>
    /// at the boundary so misconfigured options surface immediately as
    /// <see cref="ArgumentOutOfRangeException"/> instead of becoming a silent
    /// no-op timeout or an unbounded wait deeper in the send path.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> is the only
    /// negative <see cref="TimeSpan"/> permitted; it is the sentinel for
    /// "wait forever".
    /// </summary>
    public void Validate()
    {
        if (SendTimeout < TimeSpan.Zero && SendTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(
                nameof(SendTimeout),
                SendTimeout,
                $"{nameof(SendTimeout)} must be non-negative, or Timeout.InfiniteTimeSpan for no timeout.");
    }
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
