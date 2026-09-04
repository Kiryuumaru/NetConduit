using NetConduit.Models;

namespace NetConduit.Mesh.UnitTests;

public class MeshMultiplexerOptionsTests
{
    private static MeshMultiplexerOptions Valid() => new() { NodeId = "node-1" };

    [Fact]
    public void Validate_AcceptsDefaults()
    {
        var ex = Record.Exception(() => Valid().Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_RejectsInvalidNodeId()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "bad:id" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_AcceptsValidPoolId()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", PoolId = "pool-1" };
        var ex = Record.Exception(() => opts.Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_RejectsInvalidPoolId()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", PoolId = "bad/pool" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsBadMaxHops(int v)
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", MaxHops = v };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_RejectsNonPositiveRouteTimeout()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", RouteTimeout = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_AcceptsNegativeOneMaxRouteRetriesAsUnbounded()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", MaxRouteRetries = -1 };
        opts.Validate();
    }

    [Fact]
    public void Validate_RejectsMaxRouteRetriesBelowMinusOne()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", MaxRouteRetries = -2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_RejectsNegativeRecomputeDebounce()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", RecomputeDebounce = TimeSpan.FromMilliseconds(-1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_RejectsNegativeTopologyAntiEntropyInterval()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", TopologyAntiEntropyInterval = TimeSpan.FromMilliseconds(-1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_RejectsTooSmallTopologyMessageSize()
    {
        var opts = new MeshMultiplexerOptions { NodeId = "n", MaxTopologyMessageSize = 32 };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void DefaultChannelOptions_IsInitialized()
    {
        Assert.NotNull(Valid().DefaultChannelOptions);
        Assert.IsType<DefaultChannelOptions>(Valid().DefaultChannelOptions);
    }
}
