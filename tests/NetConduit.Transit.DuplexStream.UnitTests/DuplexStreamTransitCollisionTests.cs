namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitCollisionTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    [Fact]
    public async Task OpenDuplexStream_WriteIdCollision_ThrowsChannelExists()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var preExisting = client.OpenChannel("taken>>");
        Assert.NotNull(preExisting);

        var ex = Assert.Throws<MultiplexerException>(() =>
            client.OpenDuplexStream("taken"));
        Assert.Equal(ErrorCode.ChannelExists, ex.ErrorCode);

        Assert.Same(preExisting, client.GetWriteChannel("taken>>"));
        Assert.Null(client.GetReadChannel("taken<<"));
    }
}
