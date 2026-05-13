using NetConduit.Interfaces;

namespace NetConduit.Transit.Stream.UnitTests;

public sealed class StreamTransitSmokeTests
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
    public async Task OpenStream_Then_AcceptStream_TransfersBytes()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        await using var write = client.OpenStream("smoke");
        await using var read = await server.AcceptStreamAsync("smoke");

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await write.WriteAsync(payload);

        var buf = new byte[payload.Length];
        var n = await read.ReadAsync(buf);
        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, buf);
    }

    [Fact]
    public async Task AcceptStream_PendingUntilOtherSideOpens()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        await using var pending = server.AcceptStream("late");
        Assert.False(pending.CanRead && pending.CanWrite);

        await using var writer = client.OpenStream("late");
        await pending.WaitForReadyAsync();
        Assert.True(pending.IsReady);
    }
}
