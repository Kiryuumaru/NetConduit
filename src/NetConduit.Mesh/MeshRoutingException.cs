namespace NetConduit.Mesh;

/// <summary>
/// Thrown when the mesh cannot route to a target node.
/// </summary>
public sealed class MeshRoutingException : Exception
{
    /// <summary>The target node ID the mesh attempted to reach.</summary>
    public string TargetNodeId { get; }

    /// <summary>Initialise a new routing exception.</summary>
    public MeshRoutingException(string targetNodeId, string message)
        : base(message)
    {
        TargetNodeId = targetNodeId;
    }

    /// <summary>Initialise a new routing exception with an inner exception.</summary>
    public MeshRoutingException(string targetNodeId, string message, Exception innerException)
        : base(message, innerException)
    {
        TargetNodeId = targetNodeId;
    }
}
