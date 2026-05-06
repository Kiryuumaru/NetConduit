namespace NetConduit;

/// <summary>
/// Event data raised when a channel is closed on the multiplexer level.
/// </summary>
public sealed class ChannelClosedEventArgs(string channelId, Exception? exception) : EventArgs
{
    /// <summary>The string identifier of the closed channel.</summary>
    public string ChannelId { get; } = channelId;

    /// <summary>The exception that caused the close, if any.</summary>
    public Exception? Exception { get; } = exception;
}
