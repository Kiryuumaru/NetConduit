namespace NetConduit.Mesh.UnitTests;

public class TopologyWireFormatTests
{
    [Fact]
    public void Encode_Decode_RoundTrip()
    {
        var entries = new[]
        {
            new TopologyEntry("A", 1, "us", new[] { "B", "C" }),
            new TopologyEntry("B", 5, null, new[] { "A" }),
        };

        byte[] frame = TopologyWireFormat.Encode(entries);
        Assert.True(frame.Length > 4);

        // Drop the 4-byte length prefix to feed DecodePayload directly.
        var decoded = TopologyWireFormat.DecodePayload(frame.AsSpan(4));
        Assert.Equal(2, decoded.Count);

        var a = decoded.Single(e => e.NodeId == "A");
        Assert.Equal(1, a.Version);
        Assert.Equal("us", a.PoolId);
        Assert.Equal(new[] { "B", "C" }, a.Neighbors);

        var b = decoded.Single(e => e.NodeId == "B");
        Assert.Equal(5, b.Version);
        Assert.Null(b.PoolId);
    }

    [Fact]
    public void Encode_LengthPrefixIsBigEndian()
    {
        var entries = new[] { new TopologyEntry("A", 1, null, Array.Empty<string>()) };
        byte[] frame = TopologyWireFormat.Encode(entries);

        uint length = ((uint)frame[0] << 24) | ((uint)frame[1] << 16) | ((uint)frame[2] << 8) | frame[3];
        Assert.Equal((uint)(frame.Length - 4), length);
    }

    [Fact]
    public async Task ReadFrameAsync_ReadsFullFrame()
    {
        var entries = new[] { new TopologyEntry("alpha", 42, "p", new[] { "beta" }) };
        byte[] frame = TopologyWireFormat.Encode(entries);

        using var stream = new MemoryStream(frame);
        var result = await TopologyWireFormat.ReadFrameAsync(stream, 1024 * 1024, default);

        Assert.Single(result);
        Assert.Equal("alpha", result[0].NodeId);
        Assert.Equal(42, result[0].Version);
        Assert.Equal("p", result[0].PoolId);
        Assert.Equal(new[] { "beta" }, result[0].Neighbors);
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsZeroLengthPrefix()
    {
        byte[] frame = new byte[] { 0, 0, 0, 0 };
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TopologyWireFormat.ReadFrameAsync(stream, 1024, default));
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsOversizedLengthPrefix()
    {
        byte[] frame = new byte[] { 0, 0x10, 0, 0 }; // 1 MiB length prefix
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TopologyWireFormat.ReadFrameAsync(stream, 1024, default));
    }

    [Fact]
    public async Task ReadFrameAsync_PrematureEofThrows()
    {
        byte[] frame = TopologyWireFormat.Encode(new[]
        {
            new TopologyEntry("A", 1, null, Array.Empty<string>()),
        });
        // Truncate
        byte[] truncated = frame.AsSpan(0, frame.Length - 5).ToArray();
        using var stream = new MemoryStream(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await TopologyWireFormat.ReadFrameAsync(stream, 1024 * 1024, default));
    }

    [Fact]
    public void DecodePayload_RejectsMalformedJson()
    {
        byte[] payload = "not json"u8.ToArray();
        Assert.ThrowsAny<Exception>(() => TopologyWireFormat.DecodePayload(payload));
    }

    [Fact]
    public void DecodePayload_RejectsMissingVersion()
    {
        byte[] payload = "{\"nodes\":[]}"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => TopologyWireFormat.DecodePayload(payload));
    }

    [Fact]
    public void DecodePayload_RejectsUnsupportedVersion()
    {
        byte[] payload = "{\"v\":99,\"nodes\":[]}"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => TopologyWireFormat.DecodePayload(payload));
    }

    [Fact]
    public void DecodePayload_AcceptsUnicodeNodeIds()
    {
        var entries = new[]
        {
            new TopologyEntry("ノードA", 1, "プール1", new[] { "ノードB" }),
            new TopologyEntry("ノードB", 1, null, new[] { "ノードA" }),
        };
        byte[] frame = TopologyWireFormat.Encode(entries);
        var decoded = TopologyWireFormat.DecodePayload(frame.AsSpan(4));
        Assert.Equal("ノードA", decoded[0].NodeId);
        Assert.Equal("プール1", decoded[0].PoolId);
        Assert.Equal("ノードB", decoded[1].NodeId);
    }
}
