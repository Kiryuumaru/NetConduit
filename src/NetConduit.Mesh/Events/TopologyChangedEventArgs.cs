namespace NetConduit.Mesh.Events;

/// <summary>
/// Raised when the mesh topology view changes (a node entry was added, removed, or replaced).
/// </summary>
public sealed class TopologyChangedEventArgs(int knownNodeCount, int reachableNodeCount) : EventArgs
{
    /// <summary>Total nodes present in the local adjacency map (may include unreachable ones pending GC).</summary>
    public int KnownNodeCount { get; } = knownNodeCount;

    /// <summary>Subset of <see cref="KnownNodeCount"/> currently reachable through the mesh.</summary>
    public int ReachableNodeCount { get; } = reachableNodeCount;
}
