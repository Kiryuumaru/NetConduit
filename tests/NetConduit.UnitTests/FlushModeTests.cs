namespace NetConduit.UnitTests;

public sealed class FlushModeTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(FlushMode flushMode)
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            FlushMode = flushMode,
            FlushInterval = TimeSpan.FromMilliseconds(50),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        return (client, server);
    }

    [Fact]
    public async Task Immediate_DataArrivesWithoutManualFlush()
    {
        var (client, server) = CreatePair(FlushMode.Immediate);
        await Task.WhenAll(client.Start(), server.Start());

        var wch = await client.OpenChannelAsync("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        await wch.WriteAsync(new byte[] { 1, 2, 3 });

        byte[] buf = new byte[3];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Batched_DataArrivesAfterInterval()
    {
        var (client, server) = CreatePair(FlushMode.Batched);
        await Task.WhenAll(client.Start(), server.Start());

        var wch = await client.OpenChannelAsync("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        await wch.WriteAsync(new byte[] { 10, 20, 30 });

        // Data should arrive after the batch interval (50ms)
        byte[] buf = new byte[3];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 10, 20, 30 }, buf);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Manual_DataArrivesAfterExplicitFlush()
    {
        var (client, server) = CreatePair(FlushMode.Manual);
        await Task.WhenAll(client.Start(), server.Start());

        var wch = await client.OpenChannelAsync("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        await wch.WriteAsync(new byte[] { 42 });

        // Trigger explicit flush
        await client.FlushAsync();

        byte[] buf = new byte[1];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(1, read);
        Assert.Equal(42, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
