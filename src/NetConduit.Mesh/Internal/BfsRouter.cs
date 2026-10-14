namespace NetConduit.Mesh.Internal;

/// <summary>
/// Pure BFS-over-adjacency router. Finds shortest paths from a source node to every reachable node.
/// Tie-break order for equal-length paths: prefer next-hop in same pool as the source (if any),
/// then lexicographic by next-hop node ID. Hop count is bounded by <c>maxHops</c>.
/// </summary>
internal static class BfsRouter
{
    /// <summary>
    /// Compute routes from <paramref name="sourceNodeId"/> through the snapshot.
    /// Returns a map of <c>target -&gt; (nextHop, hopCount)</c>. Excludes the source itself.
    /// </summary>
    internal static Dictionary<string, RouteHop> ComputeRoutes(
        AdjacencySnapshot snapshot,
        string sourceNodeId,
        int maxHops)
    {
        var routes = new Dictionary<string, RouteHop>(StringComparer.Ordinal);
        if (!snapshot.ContainsNode(sourceNodeId))
        {
            return routes;
        }

        string? sourcePool = snapshot.PoolOf(sourceNodeId);

        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceNodeId };
        var queue = new Queue<BfsFrame>();

        // Seed BFS with direct neighbors. Sort them by tie-break order so the first-discovered
        // route to any target is also the preferred one.
        var seeds = snapshot.NeighborsOf(sourceNodeId).ToArray();
        SortByTieBreak(seeds, snapshot, sourcePool);

        foreach (string neighbor in seeds)
        {
            if (visited.Add(neighbor))
            {
                routes[neighbor] = new RouteHop(neighbor, 1);
                if (maxHops > 1)
                {
                    queue.Enqueue(new BfsFrame(neighbor, neighbor, 1));
                }
            }
        }

        while (queue.Count > 0)
        {
            var frame = queue.Dequeue();
            if (frame.HopCount >= maxHops)
            {
                continue;
            }

            var children = snapshot.NeighborsOf(frame.Current).ToArray();
            SortByTieBreak(children, snapshot, sourcePool);

            foreach (string child in children)
            {
                if (visited.Add(child))
                {
                    routes[child] = new RouteHop(frame.NextHop, frame.HopCount + 1);
                    queue.Enqueue(new BfsFrame(child, frame.NextHop, frame.HopCount + 1));
                }
            }
        }

        return routes;
    }

    private static void SortByTieBreak(string[] candidates, AdjacencySnapshot snapshot, string? sourcePool)
    {
        if (candidates.Length <= 1)
        {
            return;
        }

        Array.Sort(candidates, (a, b) =>
        {
            // Same-pool candidates first (only matters if sourcePool != null).
            if (sourcePool is not null)
            {
                bool aSame = string.Equals(snapshot.PoolOf(a), sourcePool, StringComparison.Ordinal);
                bool bSame = string.Equals(snapshot.PoolOf(b), sourcePool, StringComparison.Ordinal);
                if (aSame != bSame)
                {
                    return aSame ? -1 : 1;
                }
            }
            return string.CompareOrdinal(a, b);
        });
    }

    private readonly record struct BfsFrame(string Current, string NextHop, int HopCount);
}

/// <summary>A computed next-hop and hop count for a destination node.</summary>
internal readonly record struct RouteHop(string NextHopNodeId, int HopCount);
