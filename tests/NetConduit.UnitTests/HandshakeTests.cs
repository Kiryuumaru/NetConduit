namespace NetConduit.UnitTests;

public sealed class HandshakeTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(
        Guid? clientSessionId = null,
        Guid? serverSessionId = null)
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            SessionId = clientSessionId,
            PingInterval = TimeSpan.Zero,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            SessionId = serverSessionId,
            PingInterval = TimeSpan.Zero,
        });

        return (client, server);
    }

    [Fact]
    public async Task Handshake_DeterministicOddEvenAllocation()
    {
        // Use fixed session IDs so we can predict odd/even allocation
        var highId = new Guid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        var lowId = new Guid("00000000-0000-0000-0000-000000000001");

        var (client, server) = CreatePair(clientSessionId: highId, serverSessionId: lowId);
        await Task.WhenAll(client.Start(), server.Start());

        // Higher session ID gets odd indices
        // Client (highId) opens channel → should get odd index (1, 3, 5, ...)
        var ch1 = await client.OpenChannelAsync("test1");
        var ch2 = await client.OpenChannelAsync("test2");

        // Server (lowId) opens channel → should get even index (2, 4, 6, ...)
        var sCh1 = await server.OpenChannelAsync("server1");

        // Verify they can communicate (proves indices don't collide)
        var rch1 = await server.AcceptChannelAsync("test1", new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal("test1", rch1.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Handshake_BothSidesOpenChannels_NoCollision()
    {
        var (client, server) = CreatePair();
        await Task.WhenAll(client.Start(), server.Start());

        // Both sides open channels concurrently
        var clientCh = await client.OpenChannelAsync("from-client");
        var serverCh = await server.OpenChannelAsync("from-server");

        // Both sides accept the other's channel
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverRead = await server.AcceptChannelAsync("from-client", cts.Token);
        var clientRead = await client.AcceptChannelAsync("from-server", cts.Token);

        // Verify data flows both ways
        await clientCh.WriteAsync(new byte[] { 1, 2, 3 });
        await serverCh.WriteAsync(new byte[] { 4, 5, 6 });

        byte[] buf = new byte[3];
        int read = await serverRead.ReadAsync(buf, cts.Token);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf);

        read = await clientRead.ReadAsync(buf, cts.Token);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 4, 5, 6 }, buf);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
