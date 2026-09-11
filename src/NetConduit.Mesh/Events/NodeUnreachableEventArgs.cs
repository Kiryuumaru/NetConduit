namespace NetConduit.Mesh.Events;

/// <summary>
/// Raised when a remote node becomes unreachable through the mesh.
/// </summary>
public sealed class NodeUnreachableEventArgs(string nodeId) : EventArgs
{
    /// <summary>The remote node identifier that became unreachable.</summary>
    public string NodeId { get; } = nodeId;
}
