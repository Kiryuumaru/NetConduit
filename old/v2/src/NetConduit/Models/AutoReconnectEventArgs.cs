namespace NetConduit.Models;

/// <summary>
/// Event arguments for auto-reconnect attempts.
/// </summary>
public sealed class AutoReconnectEventArgs
{
    /// <summary>
    /// The attempt number (1-based).
    /// </summary>
    public int AttemptNumber { get; init; }
    
    /// <summary>
    /// Maximum configured attempts.
    /// </summary>
    public int MaxAttempts { get; init; }
    
    /// <summary>
    /// The delay before the next attempt.
    /// </summary>
    public TimeSpan NextDelay { get; init; }
    
    /// <summary>
    /// The exception from the last connection attempt, if any.
    /// </summary>
    public Exception? LastException { get; init; }
    
    /// <summary>
    /// Whether this is a reconnection attempt (true) or initial connection (false).
    /// </summary>
    public bool IsReconnecting { get; init; }
    
    /// <summary>
    /// Set to true to cancel the reconnection process.
    /// </summary>
    public bool Cancel { get; set; }
}
