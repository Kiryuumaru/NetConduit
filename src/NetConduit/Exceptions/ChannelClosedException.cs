namespace NetConduit;

/// <summary>
/// Exception thrown when attempting to use a closed channel.
/// </summary>
public class ChannelClosedException : Exception
{
    /// <summary>
    /// Gets the channel ID that was closed.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the reason for channel closure.
    /// </summary>
    public ChannelCloseReason CloseReason { get; }

    /// <summary>
    /// Creates a new instance of <see cref="ChannelClosedException"/>.
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="closeReason">The reason for closure.</param>
    public ChannelClosedException(string channelId, ChannelCloseReason closeReason)
        : base($"Channel '{channelId}' is closed. Reason: {closeReason}")
    {
        ChannelId = channelId;
        CloseReason = closeReason;
    }

    /// <summary>
    /// Creates a new instance of <see cref="ChannelClosedException"/> with an inner exception.
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="closeReason">The reason for closure.</param>
    /// <param name="innerException">The inner exception.</param>
    public ChannelClosedException(string channelId, ChannelCloseReason closeReason, Exception? innerException)
        : base($"Channel '{channelId}' is closed. Reason: {closeReason}", innerException)
    {
        ChannelId = channelId;
        CloseReason = closeReason;
    }
}
