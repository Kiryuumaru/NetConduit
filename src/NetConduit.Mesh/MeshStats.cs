using System.Threading;

namespace NetConduit.Mesh;

/// <summary>
/// Aggregated statistics for a mesh multiplexer.
/// </summary>
public sealed class MeshStats
{
    private long _routesOpened;
    private long _routesFailed;
    private long _relayBytesForwarded;
    private long _topologyMessagesSent;
    private long _topologyMessagesReceived;
    private long _activeSubMultiplexers;
    private long _activeRelays;

    /// <summary>Currently active routed sub-multiplexers (opener + acceptor sides counted separately).</summary>
    public long ActiveSubMultiplexers => Interlocked.Read(ref _activeSubMultiplexers);

    /// <summary>Currently active relay sessions on this node.</summary>
    public long ActiveRelays => Interlocked.Read(ref _activeRelays);

    /// <summary>Total routes opened over the lifetime of this mesh (including reroutes).</summary>
    public long RoutesOpened => Interlocked.Read(ref _routesOpened);

    /// <summary>Total routes that ultimately failed (after all retries).</summary>
    public long RoutesFailed => Interlocked.Read(ref _routesFailed);

    /// <summary>Total bytes forwarded by relay sessions on this node.</summary>
    public long RelayBytesForwarded => Interlocked.Read(ref _relayBytesForwarded);

    /// <summary>Total topology messages sent to neighbors.</summary>
    public long TopologyMessagesSent => Interlocked.Read(ref _topologyMessagesSent);

    /// <summary>Total topology messages received from neighbors.</summary>
    public long TopologyMessagesReceived => Interlocked.Read(ref _topologyMessagesReceived);

    internal void IncrementSubMultiplexers() => Interlocked.Increment(ref _activeSubMultiplexers);

    internal void DecrementSubMultiplexers() => Interlocked.Decrement(ref _activeSubMultiplexers);

    internal void IncrementRelays() => Interlocked.Increment(ref _activeRelays);

    internal void DecrementRelays() => Interlocked.Decrement(ref _activeRelays);

    internal void IncrementRoutesOpened() => Interlocked.Increment(ref _routesOpened);

    internal void IncrementRoutesFailed() => Interlocked.Increment(ref _routesFailed);

    internal void AddRelayBytes(long count) => Interlocked.Add(ref _relayBytesForwarded, count);

    internal void IncrementTopologySent() => Interlocked.Increment(ref _topologyMessagesSent);

    internal void IncrementTopologyReceived() => Interlocked.Increment(ref _topologyMessagesReceived);
}
