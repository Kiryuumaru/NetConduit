namespace NetConduit.Mesh.Internal;

/// <summary>
/// Adjacency map of the mesh. Per-node CRDT using a Last-Writer-Wins register keyed by version.
/// Higher version always wins. Equal version keeps the existing entry (stable).
/// </summary>
/// <remarks>
/// This type is NOT thread-safe. The owning <c>MeshMultiplexer</c> serializes mutations on a single
/// background loop. Read snapshots are produced under that same lock and handed to BFS.
/// </remarks>
internal sealed class AdjacencyMap
{
    private readonly Dictionary<string, NodeEntry> _nodes = new(StringComparer.Ordinal);

    /// <summary>Current number of known nodes (including the local node).</summary>
    internal int Count => _nodes.Count;

    /// <summary>Try get the entry for a node (live reference, do not mutate without owning the map lock).</summary>
    internal bool TryGet(string nodeId, out NodeEntry entry)
    {
        return _nodes.TryGetValue(nodeId, out entry!);
    }

    /// <summary>Enumerate all known node IDs.</summary>
    internal IEnumerable<string> Nodes => _nodes.Keys;

    /// <summary>
    /// Apply a single advertisement for a node. Returns <c>true</c> if the local state changed
    /// (and therefore should be re-broadcast and trigger a topology recompute).
    /// </summary>
    internal bool Apply(string nodeId, long version, string? poolId, IReadOnlyCollection<string> neighbors)
    {
        if (version < 0)
        {
            return false;
        }

        if (_nodes.TryGetValue(nodeId, out NodeEntry? existing))
        {
            if (version <= existing.Version)
            {
                return false;
            }

            existing.Version = version;
            existing.PoolId = poolId;
            existing.Neighbors.Clear();
            foreach (string n in neighbors)
            {
                if (!string.Equals(n, nodeId, StringComparison.Ordinal))
                {
                    existing.Neighbors.Add(n);
                }
            }
            return true;
        }

        var fresh = new NodeEntry { Version = version, PoolId = poolId };
        foreach (string n in neighbors)
        {
            if (!string.Equals(n, nodeId, StringComparison.Ordinal))
            {
                fresh.Neighbors.Add(n);
            }
        }
        _nodes[nodeId] = fresh;
        return true;
    }

    /// <summary>Remove a node from the map entirely. Returns whether the node was present.</summary>
    internal bool Remove(string nodeId)
    {
        return _nodes.Remove(nodeId);
    }

    /// <summary>
    /// Build a frozen snapshot suitable for handing to <see cref="BfsRouter"/>.
    /// Snapshot only includes nodes whose advertised neighbor links are bidirectional
    /// (both endpoints list each other) — guards against half-known links.
    /// </summary>
    internal AdjacencySnapshot CreateSnapshot()
    {
        var adjacency = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var pools = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (nodeId, entry) in _nodes)
        {
            pools[nodeId] = entry.PoolId;

            var validatedNeighbors = new List<string>(entry.Neighbors.Count);
            foreach (string neighborId in entry.Neighbors)
            {
                if (_nodes.TryGetValue(neighborId, out NodeEntry? neighborEntry) &&
                    neighborEntry.Neighbors.Contains(nodeId))
                {
                    validatedNeighbors.Add(neighborId);
                }
            }
            validatedNeighbors.Sort(StringComparer.Ordinal);
            adjacency[nodeId] = validatedNeighbors.ToArray();
        }

        return new AdjacencySnapshot(adjacency, pools);
    }
}

/// <summary>Immutable adjacency snapshot used by BFS. Edges are bidirectional only.</summary>
internal sealed class AdjacencySnapshot
{
    private readonly Dictionary<string, string[]> _adjacency;
    private readonly Dictionary<string, string?> _pools;

    internal AdjacencySnapshot(Dictionary<string, string[]> adjacency, Dictionary<string, string?> pools)
    {
        _adjacency = adjacency;
        _pools = pools;
    }

    internal int NodeCount => _adjacency.Count;

    internal bool ContainsNode(string nodeId) => _adjacency.ContainsKey(nodeId);

    internal IReadOnlyList<string> NeighborsOf(string nodeId)
    {
        return _adjacency.TryGetValue(nodeId, out string[]? neighbors) ? neighbors : Array.Empty<string>();
    }

    internal string? PoolOf(string nodeId)
    {
        return _pools.TryGetValue(nodeId, out string? pool) ? pool : null;
    }
}
