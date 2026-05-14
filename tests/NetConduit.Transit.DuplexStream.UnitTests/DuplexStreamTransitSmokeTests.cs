using NetConduit.Interfaces;

namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitSmokeTests
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
    public async Task OpenDuplexStream_Bidirectional_RoundTrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenDuplexStreamAsync("dx");
        var serverTask = server.AcceptDuplexStreamAsync("dx");

        await using var clientSide = await clientTask;
        await using var serverSide = await serverTask;

        var clientToServer = new byte[] { 10, 20, 30 };
        await clientSide.WriteAsync(clientToServer);

        var sbuf = new byte[clientToServer.Length];
        Assert.Equal(clientToServer.Length, await serverSide.ReadAsync(sbuf));
        Assert.Equal(clientToServer, sbuf);

        var serverToClient = new byte[] { 40, 50, 60, 70 };
        await serverSide.WriteAsync(serverToClient);

        var cbuf = new byte[serverToClient.Length];
        Assert.Equal(serverToClient.Length, await clientSide.ReadAsync(cbuf));
        Assert.Equal(serverToClient, cbuf);
    }
}
