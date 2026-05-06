namespace NetConduit;

/// <summary>
/// Event data raised when the channel itself is closed.
/// </summary>
public sealed class ChannelCloseEventArgs(ChannelCloseReason reason, Exception? exception) : EventArgs
{
    /// <summary>The reason the channel was closed.</summary>
    public ChannelCloseReason Reason { get; } = reason;

    /// <summary>The exception that caused the close, if any.</summary>
    public Exception? Exception { get; } = exception;
}
