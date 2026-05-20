using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

public sealed class DeltaMessageTransitChannelIdValidationTests
{
    private static StreamMultiplexer CreateIdleMux()
    {
        var duplex = new DuplexMemoryStream();
        return StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
    }

    [Theory]
    [InlineData("foo>>")]
    [InlineData("foo<<")]
    [InlineData("a>>b")]
    [InlineData("a<<b")]
    public void OpenDeltaMessageTransit_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() =>
            mux.OpenDeltaMessageTransit(channelId, JsonContext.Default.JsonObject));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Theory]
    [InlineData("foo>>")]
    [InlineData("foo<<")]
    public void AcceptDeltaMessageTransit_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() =>
            mux.AcceptDeltaMessageTransit(channelId, JsonContext.Default.JsonObject));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Fact]
    public async Task AcceptDeltaMessageTransit_WhenSecondStepThrows_DoesNotLeakReadChannel()
    {
        await using var mux = CreateStartedMux();

        // Pre-occupy the inbound-suffix id so AcceptDeltaMessageTransit's second
        // step (OpenChannel of base + InboundSuffix) collides in _idToIndex.
        using var _ = mux.OpenChannel("delta" + DeltaMessageTransitExtensions.InboundSuffix);

        var idsBefore = mux.ActiveChannelIds.ToArray();

        Assert.Throws<MultiplexerException>(() =>
            mux.AcceptDeltaMessageTransit("delta", JsonContext.Default.JsonObject));

        var idsAfter = mux.ActiveChannelIds.ToArray();
        Assert.Equal(idsBefore, idsAfter);
        Assert.DoesNotContain("delta" + DeltaMessageTransitExtensions.OutboundSuffix, idsAfter);
    }

    private static StreamMultiplexer CreateStartedMux()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();
        return mux;
    }
}
