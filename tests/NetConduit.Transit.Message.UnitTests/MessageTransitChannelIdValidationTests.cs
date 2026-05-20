using System.Text.Json.Serialization;

namespace NetConduit.Transit.Message.UnitTests;

public sealed class MessageTransitChannelIdValidationTests
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
    public void OpenMessageTransit_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() =>
            mux.OpenMessageTransit<SmokeMessage, SmokeMessage>(
                channelId,
                SmokeContext.Default.SmokeMessage,
                SmokeContext.Default.SmokeMessage));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Theory]
    [InlineData("foo>>")]
    [InlineData("foo<<")]
    public void AcceptMessageTransit_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() =>
            mux.AcceptMessageTransit<SmokeMessage, SmokeMessage>(
                channelId,
                SmokeContext.Default.SmokeMessage,
                SmokeContext.Default.SmokeMessage));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Fact]
    public async Task OpenMessageTransit_TwoId_WhenSecondStepThrows_DoesNotLeakWriteChannel()
    {
        await using var mux = CreateStartedMux();

        Assert.Empty(mux.ActiveChannelIds);

        Assert.Throws<ArgumentException>(() =>
            mux.OpenMessageTransit(
                writeChannelId: "msg/write",
                readChannelId: string.Empty,
                SmokeContext.Default.SmokeMessage,
                SmokeContext.Default.SmokeMessage));

        Assert.Empty(mux.ActiveChannelIds);
        Assert.DoesNotContain("msg/write", mux.ActiveChannelIds);
    }

    [Fact]
    public async Task AcceptMessageTransit_WhenSecondStepThrows_DoesNotLeakReadChannel()
    {
        await using var mux = CreateStartedMux();

        // Pre-occupy the inbound-suffix id so AcceptMessageTransit's second
        // step (OpenChannel of base + InboundSuffix) collides in _idToIndex.
        using var _ = mux.OpenChannel("msg" + MessageTransitExtensions.InboundSuffix);

        var idsBefore = mux.ActiveChannelIds.ToArray();

        Assert.Throws<MultiplexerException>(() =>
            mux.AcceptMessageTransit(
                "msg",
                SmokeContext.Default.SmokeMessage,
                SmokeContext.Default.SmokeMessage));

        var idsAfter = mux.ActiveChannelIds.ToArray();
        Assert.Equal(idsBefore, idsAfter);
        Assert.DoesNotContain("msg" + MessageTransitExtensions.OutboundSuffix, idsAfter);
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
