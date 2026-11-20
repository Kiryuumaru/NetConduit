using NetConduit.Mesh.Internal;
using NetConduit.Models;

namespace NetConduit.Mesh;

/// <summary>
/// Configuration for a mesh multiplexer.
/// </summary>
public sealed record MeshMultiplexerOptions
{
    /// <summary>Local node identifier. Must be non-empty and may not contain ':' '/' '&lt;' '&gt;' '\0' or control characters.</summary>
    public required string NodeId { get; init; }

    /// <summary>Optional pool identifier used by routing tie-breaks (pool affinity never adds hops).</summary>
    public string? PoolId { get; init; }

    /// <summary>Maximum allowed hop count for routed sessions. Routes longer than this fail with <see cref="MeshRoutingException"/>.</summary>
    public int MaxHops { get; init; } = 10;

    /// <summary>How long the mesh waits for a route to land before failing an open.</summary>
    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum reroute attempts after a routed session loses its underlying path.
    /// Use <c>-1</c> (the default) for unbounded retries — the routed sub-mux keeps
    /// attempting to re-route through any available path until disposed. Use <c>0</c>
    /// for no retries, or a positive value for a bounded budget.
    /// </summary>
    public int MaxRouteRetries { get; init; } = -1;

    /// <summary>
    /// Debounce window for topology recomputation. Topology updates received within this
    /// window are coalesced into a single BFS + broadcast. Smaller = faster convergence,
    /// larger = lower CPU under churn. Use <see cref="TimeSpan.Zero"/> (the default) for
    /// immediate recompute — required for tests and topologies where a routed path can
    /// be invalidated by a single peer advertisement.
    /// </summary>
    public TimeSpan RecomputeDebounce { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Periodic re-broadcast of local topology to all neighbors, recovering from any
    /// silently-dropped topology frames. Use <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan TopologyAntiEntropyInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Maximum concurrent relay sessions this node will host as an intermediate hop.</summary>
    public int MaxConcurrentRelays { get; init; } = 100;

    /// <summary>Default slab size applied to channels of routed sub-multiplexers.</summary>
    public int DefaultSlabSize { get; init; } = 1_048_576;

    /// <summary>Ping interval applied to routed sub-multiplexers.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Ping timeout applied to routed sub-multiplexers.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum missed pings before a routed sub-multiplexer considers its underlying path dead.</summary>
    public int MaxMissedPings { get; init; } = 2;

    /// <summary>GoAway timeout applied to routed sub-multiplexers.</summary>
    public TimeSpan GoAwayTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Default channel options applied to channels of routed sub-multiplexers.</summary>
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();

    /// <summary>Maximum topology message size in bytes. Topology messages exceeding this close the topology channel.</summary>
    public int MaxTopologyMessageSize { get; init; } = 1_048_576;

    internal void Validate()
    {
        Identifiers.ValidateNodeId(NodeId, nameof(NodeId));

        if (PoolId is not null)
        {
            Identifiers.ValidatePoolId(PoolId, nameof(PoolId));
        }

        if (MaxHops < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHops), "MaxHops must be >= 1.");
        }

        if (RouteTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RouteTimeout), "RouteTimeout must be > 0.");
        }

        if (MaxRouteRetries < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRouteRetries),
                "MaxRouteRetries must be >= 0 for a bounded retry count, or -1 for unbounded retries.");
        }

        if (RecomputeDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RecomputeDebounce),
                "RecomputeDebounce must be >= 0.");
        }

        if (TopologyAntiEntropyInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TopologyAntiEntropyInterval),
                "TopologyAntiEntropyInterval must be >= 0 (Zero disables anti-entropy).");
        }

        if (MaxConcurrentRelays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRelays), "MaxConcurrentRelays must be >= 0.");
        }

        if (DefaultSlabSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultSlabSize), "DefaultSlabSize must be >= 1.");
        }

        if (PingInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PingInterval), "PingInterval must be >= 0.");
        }

        if (PingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PingTimeout), "PingTimeout must be > 0.");
        }

        if (MaxMissedPings < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMissedPings), "MaxMissedPings must be >= 1.");
        }

        if (GoAwayTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GoAwayTimeout), "GoAwayTimeout must be > 0.");
        }

        if (MaxTopologyMessageSize < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTopologyMessageSize), "MaxTopologyMessageSize must be >= 64.");
        }
    }
}
