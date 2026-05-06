namespace NetConduit.UnitTests;

public sealed class StreamMultiplexerTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
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

        return (client, server);
    }

    [Fact]
    public async Task Start_PerformsHandshake()
    {
        var (client, server) = CreatePair();

        client.Start();
        server.Start();

        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.True(client.IsRunning);
        Assert.True(server.IsRunning);
        Assert.NotEqual(Guid.Empty, client.SessionId);
        Assert.NotEqual(Guid.Empty, server.SessionId);
        Assert.Equal(client.SessionId, server.RemoteSessionId);
        Assert.Equal(server.SessionId, client.RemoteSessionId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SendsInitFrame_ServerAccepts()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writeChannel = client.OpenChannel("ch1");
        Assert.NotNull(writeChannel);
        Assert.Equal("ch1", writeChannel.ChannelId);

        var readChannel = await server.AcceptChannelAsync("ch1", new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.NotNull(readChannel);
        Assert.Equal("ch1", readChannel.ChannelId);

        // WriteChannel becomes Open after remote ACKs the INIT
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await writeChannel.WaitForReadyAsync(cts.Token);
        Assert.Equal(ChannelState.Open, writeChannel.State);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DataFlow_ClientToServer()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writeChannel = client.OpenChannel("data");
        var readChannel = await server.AcceptChannelAsync("data", new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        byte[] sent = [1, 2, 3, 4, 5];
        await writeChannel.WriteAsync(sent);

        byte[] received = new byte[10];
        int read = await readChannel.ReadAsync(received, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.Equal(5, read);
        Assert.Equal(sent, received[..5]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ClosesAllChannels()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writeChannel = client.OpenChannel("ch1");

        await client.DisposeAsync();

        Assert.Equal(ChannelState.Closed, writeChannel.State);
        Assert.False(client.IsRunning);

        await server.DisposeAsync();
    }
}
