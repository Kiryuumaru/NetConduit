namespace NetConduit.Events;

/// <summary>
/// Event data raised when a reconnection attempt begins.
/// </summary>
public sealed class ReconnectingEventArgs(int attempt) : EventArgs
{
    /// <summary>The reconnection attempt number (1-based).</summary>
    public int Attempt { get; } = attempt;
}
