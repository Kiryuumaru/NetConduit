namespace NetConduit.UnitTests;

public sealed class FlushTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        return (client, server);
    }

    [Fact]
    public async Task DataArrivesViaFlusherThread()
    {
        var (client, server) = CreatePair();
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
    public async Task ExplicitFlush_ForcesImmediateFlush()
    {
        var (client, server) = CreatePair();
        await Task.WhenAll(client.Start(), server.Start());

        var wch = await client.OpenChannelAsync("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        await wch.WriteAsync(new byte[] { 42 });
        await client.FlushAsync();

        byte[] buf = new byte[1];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(1, read);
        Assert.Equal(42, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultipleBatches_AllDataArrives()
    {
        var (client, server) = CreatePair();
        await Task.WhenAll(client.Start(), server.Start());

        var wch = await client.OpenChannelAsync("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("test", cts.Token);

        for (int i = 0; i < 10; i++)
            await wch.WriteAsync(new byte[] { (byte)i });

        byte[] buf = new byte[1];
        for (int i = 0; i < 10; i++)
        {
            int read = await rch.ReadAsync(buf, cts.Token);
            Assert.Equal(1, read);
            Assert.Equal((byte)i, buf[0]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
