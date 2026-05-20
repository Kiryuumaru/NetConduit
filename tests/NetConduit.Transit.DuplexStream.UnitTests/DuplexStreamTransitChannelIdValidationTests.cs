namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitChannelIdValidationTests
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
    [InlineData(">><<")]
    public void OpenDuplexStream_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() => mux.OpenDuplexStream(channelId));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Theory]
    [InlineData("foo>>")]
    [InlineData("foo<<")]
    public void AcceptDuplexStream_RejectsReservedSuffixInBaseChannelId(string channelId)
    {
        var mux = CreateIdleMux();
        var ex = Assert.Throws<ArgumentException>(() => mux.AcceptDuplexStream(channelId));
        Assert.Equal("channelId", ex.ParamName);
    }

    [Fact]
    public async Task OpenDuplexStream_TwoId_WhenSecondStepThrows_DoesNotLeakWriteChannel()
    {
        await using var mux = CreateStartedMux();

        Assert.Empty(mux.ActiveChannelIds);

        // Empty read id triggers ArgumentException from EncodeValidatedChannelId
        // *after* the OpenChannel step has already registered the write side.
        Assert.Throws<ArgumentException>(() =>
            mux.OpenDuplexStream(writeChannelId: "dup/write", readChannelId: string.Empty));

        Assert.Empty(mux.ActiveChannelIds);
        Assert.DoesNotContain("dup/write", mux.ActiveChannelIds);
    }

    [Fact]
    public async Task AcceptDuplexStream_WhenSecondStepThrows_DoesNotLeakReadChannel()
    {
        await using var mux = CreateStartedMux();

        // Pre-occupy the inbound-suffix id so AcceptDuplexStream's second step
        // (OpenChannel of base + InboundSuffix) collides in _idToIndex.
        using var _ = mux.OpenChannel("dup" + DuplexStreamTransitExtensions.InboundSuffix);

        var idsBefore = mux.ActiveChannelIds.ToArray();

        Assert.Throws<MultiplexerException>(() => mux.AcceptDuplexStream("dup"));

        var idsAfter = mux.ActiveChannelIds.ToArray();
        Assert.Equal(idsBefore, idsAfter);
        Assert.DoesNotContain("dup" + DuplexStreamTransitExtensions.OutboundSuffix, idsAfter);
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
