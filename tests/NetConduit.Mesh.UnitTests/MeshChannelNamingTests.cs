namespace NetConduit.Mesh.UnitTests;

public class MeshChannelNamingTests
{
    [Fact]
    public void TopologyChannelName_IsBuiltFromSenderId()
    {
        Assert.Equal("_mesh:topo:from:A", MeshChannelNaming.BuildTopologyChannel("A"));
        Assert.Equal("_mesh:topo:from:node-7", MeshChannelNaming.BuildTopologyChannel("node-7"));
        Assert.StartsWith(MeshChannelNaming.Prefix, MeshChannelNaming.BuildTopologyChannel("X"));
    }

    [Fact]
    public void BuildAndParse_RoundTrips()
    {
        string outbound = MeshChannelNaming.BuildOutboundRoute("target-1", "source-1", "session-A", 42);

        Assert.Equal("_mesh:route:target-1:source-1/session-A:42>>", outbound);

        Assert.True(MeshChannelNaming.TryParseOutboundRoute(outbound, out var info));
        Assert.NotNull(info);
        Assert.Equal("target-1", info!.TargetNodeId);
        Assert.Equal("source-1", info.SourceNodeId);
        Assert.Equal("session-A", info.MultiplexerId);
        Assert.Equal(42L, info.Nonce);
    }

    [Fact]
    public void BuildInbound_HasMatchingBase()
    {
        string outbound = MeshChannelNaming.BuildOutboundRoute("t", "s", "m", 1);
        string inbound = MeshChannelNaming.BuildInboundRoute("t", "s", "m", 1);
        Assert.EndsWith(">>", outbound);
        Assert.EndsWith("<<", inbound);
        Assert.Equal(outbound[..^2], inbound[..^2]);
    }

    [Theory]
    [InlineData("not-a-mesh-channel")]
    [InlineData("_mesh:topo>>")]
    [InlineData("_mesh:route:>>")]
    [InlineData("_mesh:route:target:>>")]
    [InlineData("_mesh:route:target:source>>")]
    [InlineData("_mesh:route:target:source/mux>>")]
    [InlineData("_mesh:route:target:source/mux:abc>>")]
    [InlineData("_mesh:route:target:source/mux:42<<")]
    [InlineData("_mesh:route::source/mux:1>>")]
    [InlineData("_mesh:route:target:/mux:1>>")]
    public void TryParseOutboundRoute_RejectsInvalid(string channelId)
    {
        Assert.False(MeshChannelNaming.TryParseOutboundRoute(channelId, out var info));
        Assert.Null(info);
    }

    [Theory]
    [InlineData("_mesh:topo>>", true)]
    [InlineData("_mesh:anything", true)]
    [InlineData("foo", false)]
    [InlineData("_meshfoo", false)]
    public void IsReserved_DetectsPrefix(string channelId, bool expected)
    {
        Assert.Equal(expected, MeshChannelNaming.IsReserved(channelId));
    }
}
