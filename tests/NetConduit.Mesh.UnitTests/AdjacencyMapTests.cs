namespace NetConduit.Mesh.UnitTests;

public class AdjacencyMapTests
{
    [Fact]
    public void Apply_AddsNewNode()
    {
        var map = new AdjacencyMap();
        bool changed = map.Apply("A", version: 1, poolId: null, neighbors: new[] { "B" });

        Assert.True(changed);
        Assert.True(map.TryGet("A", out var entry));
        Assert.Equal(1L, entry.Version);
        Assert.Contains("B", entry.Neighbors);
    }

    [Fact]
    public void Apply_HigherVersionWins()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 1, null, new[] { "B" });
        bool changed = map.Apply("A", 2, "pool", new[] { "C" });

        Assert.True(changed);
        map.TryGet("A", out var entry);
        Assert.Equal(2L, entry.Version);
        Assert.Equal("pool", entry.PoolId);
        Assert.Contains("C", entry.Neighbors);
        Assert.DoesNotContain("B", entry.Neighbors);
    }

    [Fact]
    public void Apply_LowerOrEqualVersionIgnored()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 5, null, new[] { "B" });
        Assert.False(map.Apply("A", 4, null, new[] { "C" }));
        Assert.False(map.Apply("A", 5, null, new[] { "D" }));

        map.TryGet("A", out var entry);
        Assert.Equal(5L, entry.Version);
        Assert.Contains("B", entry.Neighbors);
    }

    [Fact]
    public void Apply_StripsSelfLoops()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 1, null, new[] { "A", "B" });
        map.TryGet("A", out var entry);
        Assert.DoesNotContain("A", entry.Neighbors);
        Assert.Contains("B", entry.Neighbors);
    }

    [Fact]
    public void Apply_NegativeVersionIgnored()
    {
        var map = new AdjacencyMap();
        Assert.False(map.Apply("A", -1, null, new[] { "B" }));
        Assert.False(map.TryGet("A", out _));
    }

    [Fact]
    public void Snapshot_OnlyKeepsBidirectionalEdges()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 1, null, new[] { "B", "C" });
        map.Apply("B", 1, null, new[] { "A" });
        // C does NOT list A: half-known link.

        var snap = map.CreateSnapshot();

        Assert.Contains("B", snap.NeighborsOf("A"));
        Assert.DoesNotContain("C", snap.NeighborsOf("A"));
    }

    [Fact]
    public void Snapshot_AdvertisedNeighborWithoutEntry_IsDropped()
    {
        var map = new AdjacencyMap();
        // A claims B, but B has never been heard about.
        map.Apply("A", 1, null, new[] { "B" });
        var snap = map.CreateSnapshot();
        Assert.Empty(snap.NeighborsOf("A"));
    }

    [Fact]
    public void Remove_DeletesNode()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 1, null, Array.Empty<string>());
        Assert.True(map.Remove("A"));
        Assert.False(map.TryGet("A", out _));
        Assert.False(map.Remove("A"));
    }

    [Fact]
    public void Count_TracksDistinctNodes()
    {
        var map = new AdjacencyMap();
        map.Apply("A", 1, null, new[] { "B" });
        map.Apply("B", 1, null, new[] { "A" });
        Assert.Equal(2, map.Count);
    }
}
