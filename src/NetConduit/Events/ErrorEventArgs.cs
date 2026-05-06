namespace NetConduit;

/// <summary>
/// Event data raised when a multiplexer error occurs.
/// </summary>
public sealed class ErrorEventArgs(Exception exception) : EventArgs
{
    /// <summary>The exception that occurred.</summary>
    public Exception Exception { get; } = exception;
}
