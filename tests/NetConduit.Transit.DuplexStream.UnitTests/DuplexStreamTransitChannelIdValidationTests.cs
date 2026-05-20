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
}
