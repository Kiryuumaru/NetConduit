namespace NetConduit.Mesh.UnitTests;

public class BfsRouterTests
{
    private static AdjacencySnapshot BuildSnapshot(params (string Node, string? Pool, string[] Neighbors)[] entries)
    {
        var map = new AdjacencyMap();
        long version = 1;
        foreach (var (node, pool, neighbors) in entries)
        {
            map.Apply(node, version++, pool, neighbors);
        }
        return map.CreateSnapshot();
    }

    [Fact]
    public void ComputeRoutes_EmptyForUnknownSource()
    {
        var snap = BuildSnapshot();
        var routes = BfsRouter.ComputeRoutes(snap, "missing", 10);
        Assert.Empty(routes);
    }

    [Fact]
    public void ComputeRoutes_DirectNeighborIsOneHop()
    {
        var snap = BuildSnapshot(
            ("A", null, new[] { "B" }),
            ("B", null, new[] { "A" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", 10);
        Assert.True(routes.TryGetValue("B", out var hop));
        Assert.Equal("B", hop.NextHopNodeId);
        Assert.Equal(1, hop.HopCount);
        Assert.DoesNotContain("A", routes.Keys);
    }

    [Fact]
    public void ComputeRoutes_TwoHopChain()
    {
        var snap = BuildSnapshot(
            ("A", null, new[] { "B" }),
            ("B", null, new[] { "A", "C" }),
            ("C", null, new[] { "B" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", 10);
        Assert.Equal("B", routes["B"].NextHopNodeId);
        Assert.Equal(1, routes["B"].HopCount);
        Assert.Equal("B", routes["C"].NextHopNodeId);
        Assert.Equal(2, routes["C"].HopCount);
    }

    [Fact]
    public void ComputeRoutes_RespectsMaxHops()
    {
        var snap = BuildSnapshot(
            ("A", null, new[] { "B" }),
            ("B", null, new[] { "A", "C" }),
            ("C", null, new[] { "B", "D" }),
            ("D", null, new[] { "C" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", maxHops: 2);
        Assert.True(routes.ContainsKey("B"));
        Assert.True(routes.ContainsKey("C"));
        Assert.False(routes.ContainsKey("D"));
    }

    [Fact]
    public void ComputeRoutes_PreferSamePoolNextHop()
    {
        // A has two direct neighbors, B (same pool) and C (other pool), both leading to D.
        var snap = BuildSnapshot(
            ("A", "p1", new[] { "B", "C" }),
            ("B", "p1", new[] { "A", "D" }),
            ("C", "p2", new[] { "A", "D" }),
            ("D", "p1", new[] { "B", "C" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", 10);
        // Both candidates at 1 hop for B and C, fine. The interesting tie is at D.
        Assert.Equal("B", routes["D"].NextHopNodeId);
        Assert.Equal(2, routes["D"].HopCount);
    }

    [Fact]
    public void ComputeRoutes_TieBreakIsLexicographic()
    {
        // No pool info: pure lexicographic ordering.
        var snap = BuildSnapshot(
            ("A", null, new[] { "B", "C" }),
            ("B", null, new[] { "A", "D" }),
            ("C", null, new[] { "A", "D" }),
            ("D", null, new[] { "B", "C" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", 10);
        Assert.Equal("B", routes["D"].NextHopNodeId);
    }

    [Fact]
    public void ComputeRoutes_DisconnectedNodesNotReturned()
    {
        var snap = BuildSnapshot(
            ("A", null, new[] { "B" }),
            ("B", null, new[] { "A" }),
            ("X", null, new[] { "Y" }),
            ("Y", null, new[] { "X" }));

        var routes = BfsRouter.ComputeRoutes(snap, "A", 10);
        Assert.False(routes.ContainsKey("X"));
        Assert.False(routes.ContainsKey("Y"));
    }
}
