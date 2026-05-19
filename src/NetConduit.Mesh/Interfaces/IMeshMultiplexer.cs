using NetConduit.Interfaces;
using NetConduit.Mesh.Events;
using CoreErrorEventArgs = NetConduit.Events.ErrorEventArgs;

namespace NetConduit.Mesh.Interfaces;

/// <summary>
/// A multi-hop routing layer that exposes a full <see cref="IStreamMultiplexer"/> to any
/// reachable node by ID. Routes through intermediate neighbors invisibly using the
/// <c>_mesh:</c> channel prefix reserved on each registered neighbor multiplexer.
/// </summary>
public interface IMeshMultiplexer : IAsyncDisposable
{
    /// <summary>Identifier of the local node.</summary>
    string NodeId { get; }

    /// <summary>Optional pool identifier of the local node used by routing tie-breaks.</summary>
    string? PoolId { get; }

    /// <summary>Whether the mesh has reached its ready state (first topology exchange completed).</summary>
    bool IsReady { get; }

    /// <summary>Whether the mesh has been started and is not yet disposed.</summary>
    bool IsRunning { get; }

    /// <summary>Count of nodes currently reachable via the mesh (excludes self).</summary>
    int ReachableNodeCount { get; }

    /// <summary>Count of nodes known to the mesh (excludes self).</summary>
    int KnownNodeCount { get; }

    /// <summary>Mesh statistics.</summary>
    MeshStats Stats { get; }

    /// <summary>
    /// Snapshot the current reachability of <paramref name="nodeId"/>. Returns the
    /// node's <see cref="NodeReachableEventArgs"/> (with pool and hop count) if
    /// reachable, or <c>null</c> if not. Race-free with respect to concurrent route
    /// recomputes: the snapshot reflects exactly one routing state.
    /// </summary>
    NodeReachableEventArgs? GetReachable(string nodeId);

    /// <summary>
    /// Wait until <paramref name="nodeId"/> is reachable through the mesh and return
    /// the reachability snapshot at that moment. Race-free: if the node is already
    /// reachable at call time, returns immediately; otherwise the waiter is parked
    /// inside the route-recompute critical section so no transition is missed.
    /// </summary>
    Task<NodeReachableEventArgs> WaitForReachableAsync(string nodeId, CancellationToken ct = default);

    /// <summary>Raised once when the mesh first becomes ready.</summary>
    event EventHandler? Ready;

    /// <summary>Raised when a remote node becomes reachable.</summary>
    event EventHandler<NodeReachableEventArgs>? NodeReachable;

    /// <summary>Raised when a previously reachable node becomes unreachable.</summary>
    event EventHandler<NodeUnreachableEventArgs>? NodeUnreachable;

    /// <summary>Raised whenever the topology adjacency map changes.</summary>
    event EventHandler<TopologyChangedEventArgs>? TopologyChanged;

    /// <summary>Raised when an unhandled exception occurs on a background loop.</summary>
    event EventHandler<CoreErrorEventArgs>? Error;

    /// <summary>Register a neighbor multiplexer. The mesh will open <c>_mesh:</c>-prefixed channels on it.</summary>
    void AddNeighbor(string remoteNodeId, IStreamMultiplexer mux, string? remotePoolId = null);

    /// <summary>Unregister a neighbor multiplexer. Closes mesh-opened channels but never disposes the neighbor mux.</summary>
    void RemoveNeighbor(string remoteNodeId);

    /// <summary>Open a routed sub-multiplexer to a target node. Returns synchronously in pending state.</summary>
    IStreamMultiplexer OpenMultiplexer(string targetNodeId, string multiplexerId);

    /// <summary>Accept a routed sub-multiplexer from a specific source. Returns synchronously in pending state.</summary>
    IStreamMultiplexer AcceptMultiplexer(string sourceNodeId, string multiplexerId);

    /// <summary>Enumerate all inbound routed sub-multiplexers as they arrive.</summary>
    IAsyncEnumerable<RoutedMultiplexer> AcceptMultiplexersAsync(CancellationToken ct = default);

    /// <summary>Initiate graceful shutdown. Closes mesh channels but does not touch neighbor muxes.</summary>
    ValueTask GoAwayAsync(CancellationToken ct = default);

    /// <summary>Wait until the mesh has reached its ready state.</summary>
    Task WaitForReadyAsync(CancellationToken ct = default);
}
