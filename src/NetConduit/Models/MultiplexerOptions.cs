namespace NetConduit;

/// <summary>
/// Delegate that creates a new stream pair for connection/reconnection.
/// The returned <see cref="IStreamPair"/> will be disposed on reconnect or shutdown.
/// </summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A stream pair containing read and write streams with proper disposal.</returns>
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);

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

/// <summary>
/// Configuration options for the multiplexer.
/// </summary>
public sealed class MultiplexerOptions
{
    /// <summary>
    /// Unique session identifier for reconnection support.
    /// If null, a new GUID is generated automatically.
    /// </summary>
    public Guid? SessionId { get; init; }
    
    /// <summary>
    /// Maximum frame payload size in bytes. Default: 16MB.
    /// </summary>
    public int MaxFrameSize { get; init; } = 16 * 1024 * 1024;
    
    /// <summary>
    /// Interval between heartbeat pings. Default: 30 seconds.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Timeout waiting for pong response. Default: 10 seconds.
    /// </summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);
    
    /// <summary>
    /// Number of missed pings before connection is considered dead. Default: 3.
    /// </summary>
    public int MaxMissedPings { get; init; } = 3;
    
    /// <summary>
    /// Timeout for graceful shutdown after GOAWAY. Default: 30 seconds.
    /// </summary>
    public TimeSpan GoAwayTimeout { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Timeout for graceful shutdown operations (mux and channels). Default: 5 seconds.
    /// Used by DisposeAsync to wait for pending operations before forcing close.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    
    /// <summary>
    /// Default channel options for new channels.
    /// </summary>
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
    
    /// <summary>
    /// Flush mode for write operations. Default: Batched.
    /// </summary>
    public FlushMode FlushMode { get; init; } = FlushMode.Batched;
    
    /// <summary>
    /// Interval for batched flush when FlushMode is Batched. Default: 1ms.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(1);
    
    /// <summary>
    /// Factory delegate for creating streams. Required.
    /// Used for initial connection and auto-reconnection on transport failure.
    /// </summary>
    public required StreamFactoryDelegate StreamFactory { get; init; }
    
    /// <summary>
    /// Maximum number of auto-reconnection attempts before giving up. Default: 0 (unlimited).
    /// Set to a positive value to limit attempts.
    /// </summary>
    public int MaxAutoReconnectAttempts { get; init; } = 0;
    
    /// <summary>
    /// Initial delay between auto-reconnection attempts. Default: 1 second.
    /// </summary>
    public TimeSpan AutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    
    /// <summary>
    /// Maximum delay between auto-reconnection attempts (for exponential backoff). Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxAutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Multiplier for exponential backoff between reconnection attempts. Default: 2.0.
    /// </summary>
    public double AutoReconnectBackoffMultiplier { get; init; } = 2.0;
    
    /// <summary>
    /// Timeout for each StreamFactory call. Default: Infinite (relies on user's CancellationToken).
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    
    /// <summary>
    /// Timeout for handshake completion after connection. Default: Infinite (relies on user's CancellationToken).
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}

/// <summary>
/// Controls how frame writes are flushed to the underlying stream.
/// </summary>
 public enum FlushMode
{
    /// <summary>
    /// Flush after every frame (highest latency, lowest throughput).
    /// </summary>
    Immediate,
    
    /// <summary>
    /// Flush periodically based on FlushInterval (balanced).
    /// </summary>
    Batched,
    
    /// <summary>
    /// Never explicitly flush, rely on stream buffering (lowest latency for small writes, highest throughput).
    /// </summary>
    Manual
}
