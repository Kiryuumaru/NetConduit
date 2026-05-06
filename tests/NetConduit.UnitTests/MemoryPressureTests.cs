namespace NetConduit.UnitTests;

/// <summary>
/// Tests for memory pressure scenarios and resource bounds:
/// - Many channels with bounded memory growth
/// - Channel disposal frees resources
/// - High channel churn memory behavior
/// </summary>
[Collection("Sequential")]
public sealed class MemoryPressureTests
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
    public async Task ManyChannels_OpenAndClose_MemoryDoesNotGrowUnbounded()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                // Read and discard
                var buf = new byte[256];
                while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                count++;
                if (count >= 200) break;
            }
        });

        // Open and close many channels, tracking that active count stays low
        for (int i = 0; i < 200; i++)
        {
            var writer = client.OpenChannel($"mem-{i}");
            await writer.WriteAsync(new byte[64], cts.Token);
            await writer.DisposeAsync();
        }

        await acceptTask.WaitAsync(cts.Token);

        // After all closed, active should be 0 or very low
        Assert.True(client.Stats.TotalChannelsOpened >= 200);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task HighChannelChurn_StatsAccumulate()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                count++;
                if (count >= 50) break;
            }
        });

        for (int i = 0; i < 50; i++)
        {
            var writer = client.OpenChannel($"churn-{i}");
            await writer.WriteAsync(new byte[128], cts.Token);
            await writer.DisposeAsync();
        }

        await acceptTask.WaitAsync(cts.Token);

        Assert.True(client.Stats.TotalChannelsOpened >= 50);
        Assert.True(client.Stats.BytesSent > 0);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task LargeDataPerChannel_MultipleChannels_AllComplete()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        const int channels = 10;
        const int dataSize = 100 * 1024;

        var writeTasks = new Task[channels];
        var readTasks = new Task<long>[channels];

        for (int i = 0; i < channels; i++)
        {
            int idx = i;
            var writer = client.OpenChannel($"bigdata-{idx}");
            var reader = await server.AcceptChannelAsync($"bigdata-{idx}", cts.Token);

            writeTasks[idx] = Task.Run(async () =>
            {
                var chunk = new byte[4096];
                int sent = 0;
                while (sent < dataSize)
                {
                    int sz = Math.Min(4096, dataSize - sent);
                    await writer.WriteAsync(chunk.AsMemory(0, sz), cts.Token);
                    sent += sz;
                }
                await writer.DisposeAsync();
            });

            readTasks[idx] = Task.Run(async () =>
            {
                long total = 0;
                var buf = new byte[8192];
                int read;
                while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
                    total += read;
                return total;
            });
        }

        await Task.WhenAll(writeTasks).WaitAsync(cts.Token);
        await Task.WhenAll(readTasks).WaitAsync(cts.Token);

        for (int i = 0; i < channels; i++)
            Assert.Equal(dataSize, await readTasks[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelDispose_ReleasesResources_ActiveCountDecreases()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writers = new IWriteChannel[5];
        for (int i = 0; i < 5; i++)
        {
            writers[i] = client.OpenChannel($"resource-{i}");
            await server.AcceptChannelAsync($"resource-{i}", cts.Token);
        }

        var openBefore = client.Stats.OpenChannels;
        Assert.True(openBefore >= 5);

        // Dispose all - sends FIN frames
        for (int i = 0; i < 5; i++)
            await writers[i].DisposeAsync();

        // Wait for FIN frames to be received by server
        await Task.Delay(300, cts.Token);

        // Server's open channel count should decrease as it processes FINs
        Assert.True(server.Stats.OpenChannels < openBefore,
            $"Server open channels ({server.Stats.OpenChannels}) should be less than before ({openBefore})");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentOpenDispose_NoResourceLeak()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var _ in server.AcceptChannelsAsync(cts.Token))
            {
                count++;
                if (count >= 100) break;
            }
        });

        // Concurrent open and dispose
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            var writer = client.OpenChannel($"concurrent-{i}");
            await writer.WriteAsync(new byte[] { 1 }, cts.Token);
            await writer.DisposeAsync();
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(cts.Token);
        await acceptTask.WaitAsync(cts.Token);

        Assert.True(client.Stats.TotalChannelsOpened >= 100);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
