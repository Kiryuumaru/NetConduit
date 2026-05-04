namespace NetConduit.UnitTests;

public sealed class ControlChannelTests
{
    [Fact]
    public async Task PingPong_ServerRespondsToPing()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromSeconds(5),
            MaxMissedPings = 3,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromSeconds(5),
            MaxMissedPings = 3,
        });

        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Both sides should stay connected with keepalive
        await Task.Delay(350);

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DataFlows_WithKeepaliveEnabled()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.FromMilliseconds(50),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.FromMilliseconds(50),
        });

        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var wch = client.OpenChannel("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        // Send data while keepalive is running
        await wch.WriteAsync(new byte[] { 0xDE, 0xAD });

        // Wait a bit so pings interleave with data
        await Task.Delay(150);

        byte[] buf = new byte[2];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, buf);

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
