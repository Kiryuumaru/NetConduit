namespace NetConduit.Mesh.Internal;

/// <summary>
/// A single node's advertised state inside the mesh adjacency map.
/// Versions form a Lamport-style monotonic counter per node.
/// </summary>
internal sealed class NodeEntry
{
    internal long Version { get; set; }

    internal string? PoolId { get; set; }

    internal HashSet<string> Neighbors { get; } = new(StringComparer.Ordinal);

    internal NodeEntry Clone()
    {
        var copy = new NodeEntry
        {
            Version = Version,
            PoolId = PoolId,
        };
        foreach (string n in Neighbors)
        {
            copy.Neighbors.Add(n);
        }
        return copy;
    }
}
