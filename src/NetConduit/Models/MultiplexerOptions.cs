namespace NetConduit;

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
    /// Whether to enable reconnection support. Default: true.
    /// </summary>
    public bool EnableReconnection { get; init; } = true;
    
    /// <summary>
    /// Maximum time to wait for reconnection before discarding state. Default: 60 seconds.
    /// </summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(60);
    
    /// <summary>
    /// Buffer size for pending data during disconnection. Default: 1MB.
    /// </summary>
    public int ReconnectBufferSize { get; init; } = 1024 * 1024;
    
    /// <summary>
    /// Flush mode for write operations. Default: Batched.
    /// </summary>
    public FlushMode FlushMode { get; init; } = FlushMode.Batched;
    
    /// <summary>
    /// Interval for batched flush when FlushMode is Batched. Default: 1ms.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(1);
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
