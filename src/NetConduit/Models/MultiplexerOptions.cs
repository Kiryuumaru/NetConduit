using NetConduit.Constants;

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

    /// <summary>Default slab size per channel in bytes.</summary>
    public int DefaultSlabSize { get; init; } = FrameConstants.DefaultSlabSize;

    /// <summary>Interval between keepalive pings.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time to wait for a pong reply before considering the connection dead.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum missed pings before disconnecting.</summary>
    public int MaxMissedPings { get; init; } = 3;

    /// <summary>Time to wait during graceful shutdown for channels to drain.</summary>
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

    /// <summary>Base delay between reconnect attempts.</summary>
    public TimeSpan AutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum delay between reconnect attempts.</summary>
    public TimeSpan MaxAutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Backoff multiplier for reconnect delay.</summary>
    public double AutoReconnectBackoffMultiplier { get; init; } = 2.0;

    /// <summary>Timeout for individual StreamFactory connection attempts.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Default options applied to channels that don't specify their own.</summary>
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
}
