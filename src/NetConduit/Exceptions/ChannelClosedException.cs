namespace NetConduit;

/// <summary>
/// Thrown when an operation is attempted on a closed channel.
/// </summary>
public sealed class ChannelClosedException(string channelId, ChannelCloseReason closeReason, Exception? innerException = null)
    : Exception($"Channel '{channelId}' is closed ({closeReason}).", innerException)
{
    /// <summary>The string identifier of the closed channel.</summary>
    public string ChannelId { get; } = channelId;

    /// <summary>The reason the channel was closed.</summary>
    public ChannelCloseReason CloseReason { get; } = closeReason;
}
