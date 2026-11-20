namespace NetConduit.Mesh.Events;

/// <summary>
/// Raised when a remote node becomes reachable through the mesh.
/// </summary>
public sealed class NodeReachableEventArgs(string nodeId, string? poolId, int hopCount) : EventArgs
{
    /// <summary>The remote node identifier.</summary>
    public string NodeId { get; } = nodeId;

    /// <summary>The remote node's pool identifier, if any.</summary>
    public string? PoolId { get; } = poolId;

    /// <summary>Hop count along the currently selected path to the node (1 = direct neighbor).</summary>
    public int HopCount { get; } = hopCount;
}
