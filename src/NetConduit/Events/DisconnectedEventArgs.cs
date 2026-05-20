using NetConduit.Enums;

namespace NetConduit.Events;

/// <summary>
/// Event data raised when the transport is disconnected.
/// </summary>
public sealed class DisconnectedEventArgs(DisconnectReason reason, Exception? exception, bool willRetry = false) : EventArgs
{
    /// <summary>The reason for the disconnection.</summary>
    public DisconnectReason Reason { get; } = reason;

    /// <summary>The exception that caused the disconnection, if any.</summary>
    public Exception? Exception { get; } = exception;

    /// <summary>
    /// When <c>true</c>, this is a transient transport drop that the
    /// multiplexer will attempt to recover from automatically. Consumers that
    /// hide reconnect cycles (overlay protocols, routed sub-multiplexers)
    /// should suppress this event and wait for one with <see cref="WillRetry"/>
    /// equal to <c>false</c>, which signals terminal disconnection.
    /// </summary>
    public bool WillRetry { get; } = willRetry;
}
