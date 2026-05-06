using System.Collections.Concurrent;

namespace NetConduit.UnitTests;

[Collection("Sequential")]
public sealed class ConcurrencyTests
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
    public async Task ConcurrentMultipleChannels_AllDataTransferred()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int channelCount = 50;
        const int messageSize = 256;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var sendTasks = new Task[channelCount];
        var receiveTasks = new Task[channelCount];
        var results = new ConcurrentDictionary<string, byte[]>();

        // Throttle concurrent opens
        using var throttle = new SemaphoreSlim(20);

        for (int i = 0; i < channelCount; i++)
        {
            int idx = i;
            string channelId = $"concurrent-{idx}";

            sendTasks[i] = Task.Run(async () =>
            {
                await throttle.WaitAsync(cts.Token);
                try
                {
                    var channel = client.OpenChannel(channelId);
                    var data = new byte[messageSize];
                    Array.Fill(data, (byte)(idx % 256));
                    await channel.WriteAsync(data, cts.Token);
                    await channel.DisposeAsync();
                }
                finally
                {
                    throttle.Release();
                }
            }, cts.Token);

            receiveTasks[i] = Task.Run(async () =>
            {
                var channel = await server.AcceptChannelAsync(channelId, cts.Token);
                var received = new byte[messageSize];
                int totalRead = 0;
                while (totalRead < messageSize)
                {
                    int read = await channel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }
                results[channelId] = received[..totalRead];
            }, cts.Token);
        }

        await Task.WhenAll(sendTasks);
        await Task.WhenAll(receiveTasks);

        Assert.Equal(channelCount, results.Count);
        for (int i = 0; i < channelCount; i++)
        {
            var expected = new byte[messageSize];
            Array.Fill(expected, (byte)(i % 256));
            Assert.Equal(expected, results[$"concurrent-{i}"]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentBidirectional_AllDataCorrect()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int pairCount = 20;
        const int messageSize = 512;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var tasks = new List<Task>();
        var clientReceived = new ConcurrentDictionary<int, byte[]>();
        var serverReceived = new ConcurrentDictionary<int, byte[]>();

        for (int i = 0; i < pairCount; i++)
        {
            int idx = i;

            // Client → Server
            tasks.Add(Task.Run(async () =>
            {
                var channel = client.OpenChannel($"c2s-{idx}");
                var data = new byte[messageSize];
                Array.Fill(data, (byte)idx);
                await channel.WriteAsync(data, cts.Token);
                await channel.DisposeAsync();
            }, cts.Token));

            tasks.Add(Task.Run(async () =>
            {
                var channel = await server.AcceptChannelAsync($"c2s-{idx}", cts.Token);
                var buf = new byte[messageSize];
                int total = 0;
                while (total < messageSize)
                {
                    int r = await channel.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (r == 0) break;
                    total += r;
                }
                serverReceived[idx] = buf[..total];
            }, cts.Token));

            // Server → Client
            tasks.Add(Task.Run(async () =>
            {
                var channel = server.OpenChannel($"s2c-{idx}");
                var data = new byte[messageSize];
                Array.Fill(data, (byte)(idx + 128));
                await channel.WriteAsync(data, cts.Token);
                await channel.DisposeAsync();
            }, cts.Token));

            tasks.Add(Task.Run(async () =>
            {
                var channel = await client.AcceptChannelAsync($"s2c-{idx}", cts.Token);
                var buf = new byte[messageSize];
                int total = 0;
                while (total < messageSize)
                {
                    int r = await channel.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (r == 0) break;
                    total += r;
                }
                clientReceived[idx] = buf[..total];
            }, cts.Token));
        }

        await Task.WhenAll(tasks);

        for (int i = 0; i < pairCount; i++)
        {
            var expectedS = new byte[messageSize];
            Array.Fill(expectedS, (byte)i);
            Assert.Equal(expectedS, serverReceived[i]);

            var expectedC = new byte[messageSize];
            Array.Fill(expectedC, (byte)(i + 128));
            Assert.Equal(expectedC, clientReceived[i]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentWritersSameChannel_NoCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int writerCount = 10;
        const int writesPerWriter = 100;
        const int writeSize = 256;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var channel = client.OpenChannel("shared");
        var readChannel = await server.AcceptChannelAsync("shared", cts.Token);

        // Multiple writers write to same channel
        var writeTasks = new Task[writerCount];
        for (int w = 0; w < writerCount; w++)
        {
            int writer = w;
            writeTasks[w] = Task.Run(async () =>
            {
                for (int i = 0; i < writesPerWriter; i++)
                {
                    var data = new byte[writeSize];
                    Array.Fill(data, (byte)writer);
                    await channel.WriteAsync(data, cts.Token);
                }
            }, cts.Token);
        }

        // Read all data
        int expectedTotal = writerCount * writesPerWriter * writeSize;
        var allReceived = new byte[expectedTotal];
        int totalRead = 0;

        var readTask = Task.Run(async () =>
        {
            while (totalRead < expectedTotal)
            {
                int read = await readChannel.ReadAsync(allReceived.AsMemory(totalRead), cts.Token);
                if (read > 0)
                    totalRead += read;
                else
                    await Task.Yield();
            }
        }, cts.Token);

        await Task.WhenAll(writeTasks);
        await client.FlushAsync(cts.Token);
        await Task.Delay(50, cts.Token); // allow flush to propagate
        await channel.DisposeAsync();
        await readTask;

        Assert.Equal(expectedTotal, totalRead);

        // Each 256-byte block should be a consistent fill value (no interleaving mid-write)
        for (int offset = 0; offset < expectedTotal; offset += writeSize)
        {
            byte expected = allReceived[offset];
            for (int b = 1; b < writeSize; b++)
            {
                Assert.Equal(expected, allReceived[offset + b]);
            }
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RapidOpenClose_NoResourceLeak()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int cycles = 50;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        for (int i = 0; i < cycles; i++)
        {
            var channel = client.OpenChannel($"rapid-{i}");
            var readCh = await server.AcceptChannelAsync($"rapid-{i}", cts.Token);
            await channel.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
            await channel.DisposeAsync();

            // Read until EOF
            var buf = new byte[16];
            while (await readCh.ReadAsync(buf, cts.Token) > 0) { }
        }

        // Successfully opened and closed 50 channels without error
        Assert.Equal(cycles, client.Stats.TotalChannelsOpened);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ParallelChannelOpen_AllSucceed()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int count = 50;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Open all channels in parallel from separate tasks
        var channels = new IWriteChannel[count];
        var openTasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            openTasks[i] = Task.Run(() =>
            {
                channels[idx] = client.OpenChannel($"parallel-{idx}");
            });
        }

        await Task.WhenAll(openTasks);

        // Accept all
        var acceptTasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            acceptTasks[i] = Task.Run(async () =>
            {
                var ch = await server.AcceptChannelAsync($"parallel-{idx}", cts.Token);
                Assert.NotNull(ch);
            }, cts.Token);
        }

        await Task.WhenAll(acceptTasks);

        // All channels should become ready after server accepts
        for (int i = 0; i < count; i++)
        {
            await channels[i].WaitForReadyAsync(cts.Token);
            Assert.Equal(ChannelState.Open, channels[i].State);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
