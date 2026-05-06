namespace NetConduit;

/// <summary>
/// Event data for channel lifecycle events that identify a channel by its string ID.
/// </summary>
public sealed class ChannelEventArgs(string channelId) : EventArgs
{
    /// <summary>The string identifier of the channel.</summary>
    public string ChannelId { get; } = channelId;
}
