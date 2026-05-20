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
}
