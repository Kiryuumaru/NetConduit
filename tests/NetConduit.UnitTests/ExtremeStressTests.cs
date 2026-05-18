using System.Security.Cryptography;

namespace NetConduit.UnitTests;

/// <summary>
/// Stress tests for extreme scenarios:
/// - Many channels (thousands)
/// - Large data transfers
/// - Rapid open/close at scale
/// - Nested mux (mux over channel)
/// </summary>
[Collection("Sequential")]
public sealed class ExtremeStressTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(int slabSize = 0)
    {
        var duplex = new DuplexMemoryStream();
        var clientOpts = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        };
        var serverOpts = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        };
        if (slabSize > 0)
        {
            clientOpts = clientOpts with { DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize } };
            serverOpts = serverOpts with { DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize } };
        }
        var client = StreamMultiplexer.Create(clientOpts);
        var server = StreamMultiplexer.Create(serverOpts);
        return (client, server);
    }

    [Fact]
    public async Task Scale_HundredChannels_AllSucceed()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channelCount = 100;

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[16];
                var read = await ch.ReadAsync(buf, cts.Token);
                Assert.True(read > 0);
                count++;
                if (count >= channelCount) break;
            }
            return count;
        });

        for (int i = 0; i < channelCount; i++)
        {
            var writer = client.OpenChannel($"scale-{i}");
            await writer.WriteAsync(new byte[] { (byte)(i % 256) }, cts.Token);
            await writer.DisposeAsync();
        }

        var accepted = await acceptTask.WaitAsync(cts.Token);
        Assert.Equal(channelCount, accepted);
        Assert.True(client.Stats.TotalChannelsOpened >= channelCount);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_ThousandChannels_OpenCloseRapidly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channelCount = 1000;

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                count++;
                if (count >= channelCount) break;
            }
            return count;
        });

        for (int i = 0; i < channelCount; i++)
        {
            var writer = client.OpenChannel($"k-{i}");
            await writer.DisposeAsync();
        }

        var accepted = await acceptTask.WaitAsync(cts.Token);
        Assert.Equal(channelCount, accepted);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_LargeDataTransfer_2MB_SingleChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "big-transfer", SlabSize = 4 * 1024 * 1024 });
        var reader = await server.AcceptChannelAsync("big-transfer", cts.Token);

        const int totalSize = 2 * 1024 * 1024;
        const int chunkSize = 16384;

        var sent = new byte[totalSize];
        Random.Shared.NextBytes(sent);
        var expectedHash = System.Security.Cryptography.SHA256.HashData(sent);

        var writeTask = Task.Run(async () =>
        {
            int offset = 0;
            while (offset < totalSize)
            {
                int size = Math.Min(chunkSize, totalSize - offset);
                await writer.WriteAsync(sent.AsMemory(offset, size), cts.Token);
                offset += size;
            }
            await writer.DisposeAsync();
        });

        var received = new byte[totalSize];
        int totalRead = 0;
        while (totalRead < totalSize)
        {
            int read = await reader.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (read > 0)
                totalRead += read;
            else
                await Task.Yield();
        }

        await writeTask;

        Assert.Equal(totalSize, totalRead);
        Assert.Equal(expectedHash, System.Security.Cryptography.SHA256.HashData(received));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_ParallelDataTransfer_MultipleChannels()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channels = 5;
        const int dataPerChannel = 100 * 1024;

        var writeTasks = new Task[channels];
        var readTasks = new Task<long>[channels];

        for (int i = 0; i < channels; i++)
        {
            int idx = i;
            var writer = client.OpenChannel($"parallel-{idx}");
            var reader = await server.AcceptChannelAsync($"parallel-{idx}", cts.Token);

            writeTasks[idx] = Task.Run(async () =>
            {
                var chunk = new byte[4096];
                int sent = 0;
                while (sent < dataPerChannel)
                {
                    int size = Math.Min(4096, dataPerChannel - sent);
                    chunk.AsSpan(0, size).Fill((byte)(idx % 256));
                    await writer.WriteAsync(chunk.AsMemory(0, size), cts.Token);
                    sent += size;
                }
                await writer.DisposeAsync();
            });

            readTasks[idx] = Task.Run(async () =>
            {
                long total = 0;
                var buf = new byte[8192];
                while (total < dataPerChannel)
                {
                    int read = await reader.ReadAsync(buf, cts.Token);
                    if (read > 0)
                        total += read;
                    else
                        await Task.Yield();
                }
                return total;
            });
        }

        await Task.WhenAll(writeTasks).WaitAsync(cts.Token);
        await Task.WhenAll(readTasks).WaitAsync(cts.Token);

        for (int i = 0; i < channels; i++)
            Assert.Equal(dataPerChannel, await readTasks[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_ManySmallMessages_HighThroughput()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writer = client.OpenChannel("high-msg");
        var reader = await server.AcceptChannelAsync("high-msg", cts.Token);

        const int messageCount = 10000;
        const int messageSize = 16;
        long expectedTotal = (long)messageCount * messageSize;

        var writeTask = Task.Run(async () =>
        {
            var msg = new byte[messageSize];
            for (int i = 0; i < messageCount; i++)
            {
                msg.AsSpan().Fill((byte)(i % 256));
                await writer.WriteAsync(msg, cts.Token);
            }
            await writer.DisposeAsync();
        });

        var readTask = Task.Run(async () =>
        {
            long total = 0;
            var buf = new byte[4096];
            while (total < expectedTotal)
            {
                int read = await reader.ReadAsync(buf, cts.Token);
                if (read > 0)
                    total += read;
                else
                    await Task.Yield();
            }
            return total;
        });

        await writeTask.WaitAsync(cts.Token);
        var totalRead = await readTask.WaitAsync(cts.Token);
        Assert.Equal(expectedTotal, totalRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_HeavyData_MultiChannel_Checksums()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channels = 5;
        const int dataPerChannel = 512 * 1024;

        // Pre-generate data for each channel
        var sentData = new byte[channels][];
        var expectedHashes = new byte[channels][];
        for (int i = 0; i < channels; i++)
        {
            sentData[i] = new byte[dataPerChannel];
            new Random(i * 42).NextBytes(sentData[i]);
            expectedHashes[i] = System.Security.Cryptography.SHA256.HashData(sentData[i]);
        }

        var actualHashes = new byte[channels][];
        var writeTasks = new Task[channels];
        var readTasks = new Task[channels];

        for (int i = 0; i < channels; i++)
        {
            int idx = i;
            var writer = client.OpenChannel($"heavy-{idx}");
            var reader = await server.AcceptChannelAsync($"heavy-{idx}", cts.Token);

            writeTasks[idx] = Task.Run(async () =>
            {
                int sent = 0;
                while (sent < dataPerChannel)
                {
                    int size = Math.Min(16384, dataPerChannel - sent);
                    await writer.WriteAsync(sentData[idx].AsMemory(sent, size), cts.Token);
                    sent += size;
                }
                await writer.DisposeAsync();
            });

            readTasks[idx] = Task.Run(async () =>
            {
                var received = new byte[dataPerChannel];
                int totalRead = 0;
                while (totalRead < dataPerChannel)
                {
                    int read = await reader.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (read > 0)
                        totalRead += read;
                    else
                        await Task.Yield();
                }
                actualHashes[idx] = System.Security.Cryptography.SHA256.HashData(received.AsSpan(0, totalRead));
            });
        }

        await Task.WhenAll(writeTasks).WaitAsync(cts.Token);
        await Task.WhenAll(readTasks).WaitAsync(cts.Token);

        for (int i = 0; i < channels; i++)
            Assert.Equal(expectedHashes[i], actualHashes[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Scale_ConcurrentBidirectional_StressTest()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channelsPerSide = 5;
        const int dataSize = 50 * 1024;
        var tasks = new List<Task>();

        // Client -> Server channels
        for (int i = 0; i < channelsPerSide; i++)
        {
            int idx = i;
            var writer = client.OpenChannel($"c2s-{idx}");
            var reader = await server.AcceptChannelAsync($"c2s-{idx}", cts.Token);

            tasks.Add(Task.Run(async () =>
            {
                var data = new byte[dataSize];
                data.AsSpan().Fill((byte)idx);
                await writer.WriteAsync(data, cts.Token);
                await writer.DisposeAsync();
            }));

            tasks.Add(Task.Run(async () =>
            {
                long total = 0;
                var buf = new byte[8192];
                while (total < dataSize)
                {
                    int read = await reader.ReadAsync(buf, cts.Token);
                    if (read > 0) total += read;
                    else await Task.Yield();
                }
                Assert.Equal(dataSize, total);
            }));
        }

        // Server -> Client channels
        for (int i = 0; i < channelsPerSide; i++)
        {
            int idx = i;
            var writer = server.OpenChannel($"s2c-{idx}");
            var reader = await client.AcceptChannelAsync($"s2c-{idx}", cts.Token);

            tasks.Add(Task.Run(async () =>
            {
                var data = new byte[dataSize];
                data.AsSpan().Fill((byte)(idx + 100));
                await writer.WriteAsync(data, cts.Token);
                await writer.DisposeAsync();
            }));

            tasks.Add(Task.Run(async () =>
            {
                long total = 0;
                var buf = new byte[8192];
                while (total < dataSize)
                {
                    int read = await reader.ReadAsync(buf, cts.Token);
                    if (read > 0) total += read;
                    else await Task.Yield();
                }
                Assert.Equal(dataSize, total);
            }));
        }

        await Task.WhenAll(tasks).WaitAsync(cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
