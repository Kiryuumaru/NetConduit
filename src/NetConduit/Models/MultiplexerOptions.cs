namespace NetConduit.Models;

/// <summary>
/// Configuration for a multiplexer session.
/// </summary>
public sealed record MultiplexerOptions
{
    /// <summary>Factory that creates transport stream pairs.</summary>
    public required StreamFactoryDelegate StreamFactory { get; init; }

    /// <summary>Session identity. Auto-generated if null.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// Interval between keepalive pings. Set to <see cref="TimeSpan.Zero"/> to disable keepalive entirely
    /// (no ping/pong traffic, no missed-ping disconnect). Must be non-negative.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time to wait for a pong reply before counting a missed ping. Must be positive when keepalive is enabled
    /// (i.e. when <see cref="PingInterval"/> is greater than <see cref="TimeSpan.Zero"/>). Ignored when keepalive is disabled.
    /// </summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum missed pings before disconnecting. Must be at least 1 when keepalive is enabled.
    /// Ignored when keepalive is disabled (see <see cref="PingInterval"/>).
    /// </summary>
    public int MaxMissedPings { get; init; } = 3;

    /// <summary>Time to wait during graceful shutdown for channels to drain. Must be non-negative; zero means no drain wait.</summary>
    public TimeSpan GoAwayTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum auto-reconnect attempts when the underlying transport dies.
    /// <list type="bullet">
    /// <item><description><c>-1</c> = unlimited reconnects (default). The multiplexer keeps trying forever until disposed.</description></item>
    /// <item><description><c>0</c> = no reconnect. The first transport failure raises terminal <c>Disconnected</c>.</description></item>
    /// <item><description><c>&gt;0</c> = bounded retries. After this many consecutive failures the multiplexer gives up.</description></item>
    /// </list>
    /// Replay of in-flight stream data is enabled whenever auto-reconnect is enabled (any non-zero value).
    /// </summary>
    public int MaxAutoReconnectAttempts { get; init; } = -1;

    /// <summary>Base delay between reconnect attempts. Must be non-negative.</summary>
    public TimeSpan AutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum delay between reconnect attempts. Must be greater than or equal to <see cref="AutoReconnectDelay"/>.</summary>
    public TimeSpan MaxAutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Backoff multiplier for reconnect delay. Must be greater than or equal to 1.0.</summary>
    public double AutoReconnectBackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Timeout for individual StreamFactory connection attempts. Set to <see cref="Timeout.InfiniteTimeSpan"/> or
    /// <see cref="TimeSpan.Zero"/> to disable per-attempt timeout enforcement. Negative values other than
    /// <see cref="Timeout.InfiniteTimeSpan"/> are rejected.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Default options applied to channels that don't specify their own.</summary>
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
}
