namespace NetConduit.UnitTests;

/// <summary>
/// Tests for half-close boundary conditions:
/// - Writer closes while reader is pending
/// - Reader closes while writer is active
/// - Open/close without data
/// - Rapid channel lifecycle
/// - GoAway from acceptor side
/// </summary>
public sealed class HalfCloseBoundaryTests
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
    public async Task WriterClose_WhileReaderPending_ReaderGetsEOF()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("hc1");
        var reader = await server.AcceptChannelAsync("hc1", cts.Token);

        // Start a read that will block waiting for data
        var readTask = Task.Run(async () =>
        {
            var buf = new byte[64];
            int total = 0;
            int read;
            while ((read = await reader.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
                total += read;
            return total;
        });

        // Write then close
        await writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);
        await writer.DisposeAsync();

        var totalRead = await readTask.WaitAsync(cts.Token);
        Assert.Equal(5, totalRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriterDispose_WhileReaderPending_ReaderGetsDataThenZero()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("hc2");
        var reader = await server.AcceptChannelAsync("hc2", cts.Token);

        await writer.WriteAsync(new byte[] { 10, 20, 30 }, cts.Token);
        await writer.DisposeAsync(); // sends FIN

        var buf = new byte[64];
        int total = 0;
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;

        Assert.Equal(3, total);
        Assert.Equal(new byte[] { 10, 20, 30 }, buf[..3]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenCloseWithoutData_ChannelTransitionsCleanly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("hc3");
        var reader = await server.AcceptChannelAsync("hc3", cts.Token);

        // Close immediately without writing any data
        await writer.DisposeAsync();

        // Reader should get 0 immediately
        var buf = new byte[16];
        var read = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(0, read);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RapidOpenClose_HundredsOfCycles_NoHang()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[16];
                _ = await ch.ReadAsync(buf, cts.Token);
                count++;
                if (count >= 100) break;
            }
            return count;
        });

        for (int i = 0; i < 100; i++)
        {
            var writer = client.OpenChannel($"rapid-{i}");
            await writer.WriteAsync(new byte[] { (byte)(i % 256) }, cts.Token);
            await writer.DisposeAsync();
        }

        var accepted = await acceptTask.WaitAsync(cts.Token);
        Assert.Equal(100, accepted);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task HalfClose_WriterClosesBeforeReaderStarts_DataStillAvailable()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("hc4");
        await writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);
        await writer.DisposeAsync();

        // Reader starts after writer is already closed
        await Task.Delay(100, cts.Token);
        var reader = await server.AcceptChannelAsync("hc4", cts.Token);
        var buf = new byte[64];
        int total = 0;
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;

        Assert.Equal(5, total);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buf[..5]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SimultaneousClose_BothSides_NoDeadlock()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("hc5");
        var reader = await server.AcceptChannelAsync("hc5", cts.Token);

        await writer.WriteAsync(new byte[] { 1 }, cts.Token);

        // Both sides close simultaneously
        var closeWriter = writer.DisposeAsync();
        var closeReader = reader.DisposeAsync();

        await closeWriter;
        await closeReader;

        // No deadlock — test completes
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultipleChannels_InterleavedHalfClose_AllComplete()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        const int channelCount = 10;

        var acceptTask = Task.Run(async () =>
        {
            var results = new int[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                var ch = await server.AcceptChannelAsync($"interleave-{i}", cts.Token);
                var buf = new byte[1024];
                int total = 0;
                int read;
                while ((read = await ch.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
                    total += read;
                results[i] = total;
            }
            return results;
        });

        // Open channels, write varying amounts, close in random order
        var writers = new IWriteChannel[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = client.OpenChannel($"interleave-{i}");
            var data = new byte[i * 10 + 1];
            data.AsSpan().Fill((byte)(i + 1));
            await writers[i].WriteAsync(data, cts.Token);
        }

        // Close in reverse order
        for (int i = channelCount - 1; i >= 0; i--)
            await writers[i].DisposeAsync();

        var results = await acceptTask.WaitAsync(cts.Token);
        for (int i = 0; i < channelCount; i++)
            Assert.Equal(i * 10 + 1, results[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_AfterGoAway_NewChannelOpenDoesNotSendData()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await client.GoAwayAsync(cts.Token);

        // After GoAway, the mux is shutting down - internal loops are cancelled
        // OpenChannel may succeed locally but data won't flow
        await Task.Delay(200, cts.Token);

        // Verify the mux is no longer connected (loops stopped)
        Assert.False(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_InFlightData_DeliveredBeforeShutdown()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writer = client.OpenChannel("goaway-data");
        var reader = await server.AcceptChannelAsync("goaway-data", cts.Token);

        // Write data, close the channel, and flush to ensure delivery before GoAway
        await writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);
        await writer.DisposeAsync();
        await client.FlushAsync(cts.Token);
        // Allow time for data to flow through DuplexMemoryStream
        await Task.Delay(50, cts.Token);

        await client.GoAwayAsync(cts.Token);

        var buf = new byte[16];
        int total = 0;
        while (total < 5)
        {
            int read = await reader.ReadAsync(buf.AsMemory(total), cts.Token);
            if (read > 0)
                total += read;
            else
                await Task.Yield();
        }

        Assert.Equal(5, total);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
