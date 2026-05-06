namespace NetConduit;

/// <summary>
/// Event data raised when the transport is disconnected.
/// </summary>
public sealed class DisconnectedEventArgs(DisconnectReason reason, Exception? exception) : EventArgs
{
    /// <summary>The reason for the disconnection.</summary>
    public DisconnectReason Reason { get; } = reason;

    /// <summary>The exception that caused the disconnection, if any.</summary>
    public Exception? Exception { get; } = exception;
}
