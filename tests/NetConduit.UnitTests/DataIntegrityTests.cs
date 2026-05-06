using System.Security.Cryptography;

namespace NetConduit.UnitTests;

[Collection("Sequential")]
public sealed class DataIntegrityTests
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
    public async Task HeavyLoad_MultipleChannels_ChecksumVerification()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int channelCount = 5;
        const int messagesPerChannel = 20;
        const int messageSize = 4096;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Sender: open channels and send data with known patterns
        var sendTasks = new Task[channelCount];
        var expectedHashes = new byte[channelCount][];

        for (int i = 0; i < channelCount; i++)
        {
            int idx = i;
            sendTasks[i] = Task.Run(async () =>
            {
                var channel = client.OpenChannel($"integrity-{idx}");
                using var sha = SHA256.Create();

                for (int m = 0; m < messagesPerChannel; m++)
                {
                    var data = new byte[messageSize];
                    // Deterministic pattern based on channel + message index
                    for (int b = 0; b < messageSize; b++)
                        data[b] = (byte)((idx * 31 + m * 7 + b) % 256);

                    sha.TransformBlock(data, 0, data.Length, null, 0);
                    await channel.WriteAsync(data, cts.Token);
                }

                sha.TransformFinalBlock([], 0, 0);
                expectedHashes[idx] = sha.Hash!;
                await channel.DisposeAsync();
            }, cts.Token);
        }

        // Receiver: accept channels and verify checksums
        var receiveTasks = new Task[channelCount];
        var actualHashes = new byte[channelCount][];

        for (int i = 0; i < channelCount; i++)
        {
            int idx = i;
            receiveTasks[i] = Task.Run(async () =>
            {
                var channel = await server.AcceptChannelAsync($"integrity-{idx}", cts.Token);
                using var sha = SHA256.Create();
                var buffer = new byte[8192];
                int totalRead = 0;
                int expectedTotal = messagesPerChannel * messageSize;

                while (totalRead < expectedTotal)
                {
                    int read = await channel.ReadAsync(buffer, cts.Token);
                    if (read == 0) break;
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    totalRead += read;
                }

                sha.TransformFinalBlock([], 0, 0);
                actualHashes[idx] = sha.Hash!;
                Assert.Equal(expectedTotal, totalRead);
            }, cts.Token);
        }

        await Task.WhenAll(sendTasks);
        await Task.WhenAll(receiveTasks);

        // Verify all checksums match
        for (int i = 0; i < channelCount; i++)
        {
            Assert.Equal(expectedHashes[i], actualHashes[i]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BidirectionalData_AllBytesCorrect()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int pairCount = 5;
        const int messageCount = 20;
        const int messageSize = 1024;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var tasks = new List<Task>();

        for (int p = 0; p < pairCount; p++)
        {
            int pairIdx = p;

            // Client → Server direction
            tasks.Add(Task.Run(async () =>
            {
                var channel = client.OpenChannel($"c2s-{pairIdx}");
                for (int m = 0; m < messageCount; m++)
                {
                    var data = new byte[messageSize];
                    Array.Fill(data, (byte)(pairIdx + m));
                    await channel.WriteAsync(data, cts.Token);
                }
                await channel.DisposeAsync();
            }, cts.Token));

            tasks.Add(Task.Run(async () =>
            {
                var channel = await server.AcceptChannelAsync($"c2s-{pairIdx}", cts.Token);
                for (int m = 0; m < messageCount; m++)
                {
                    var received = new byte[messageSize];
                    int totalRead = 0;
                    while (totalRead < messageSize)
                    {
                        int read = await channel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    Assert.Equal(messageSize, totalRead);
                    Assert.All(received, b => Assert.Equal((byte)(pairIdx + m), b));
                }
            }, cts.Token));

            // Server → Client direction
            tasks.Add(Task.Run(async () =>
            {
                var channel = server.OpenChannel($"s2c-{pairIdx}");
                for (int m = 0; m < messageCount; m++)
                {
                    var data = new byte[messageSize];
                    Array.Fill(data, (byte)(pairIdx + m + 128));
                    await channel.WriteAsync(data, cts.Token);
                }
                await channel.DisposeAsync();
            }, cts.Token));

            tasks.Add(Task.Run(async () =>
            {
                var channel = await client.AcceptChannelAsync($"s2c-{pairIdx}", cts.Token);
                for (int m = 0; m < messageCount; m++)
                {
                    var received = new byte[messageSize];
                    int totalRead = 0;
                    while (totalRead < messageSize)
                    {
                        int read = await channel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    Assert.Equal(messageSize, totalRead);
                    Assert.All(received, b => Assert.Equal((byte)(pairIdx + m + 128), b));
                }
            }, cts.Token));
        }

        await Task.WhenAll(tasks);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ManyChannels_NoIndexCollision()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int channelsPerSide = 10;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Both sides open channels simultaneously
        var clientChannels = new IWriteChannel[channelsPerSide];
        var serverChannels = new IWriteChannel[channelsPerSide];

        for (int i = 0; i < channelsPerSide; i++)
        {
            clientChannels[i] = client.OpenChannel($"client-ch-{i}");
            serverChannels[i] = server.OpenChannel($"server-ch-{i}");
        }

        // Accept all channels from both sides
        var acceptTasks = new List<Task>();
        for (int i = 0; i < channelsPerSide; i++)
        {
            int idx = i;
            acceptTasks.Add(Task.Run(async () =>
            {
                var ch = await server.AcceptChannelAsync($"client-ch-{idx}", cts.Token);
                Assert.NotNull(ch);
            }, cts.Token));
            acceptTasks.Add(Task.Run(async () =>
            {
                var ch = await client.AcceptChannelAsync($"server-ch-{idx}", cts.Token);
                Assert.NotNull(ch);
            }, cts.Token));
        }

        await Task.WhenAll(acceptTasks);

        // Verify all channels can send/receive data independently
        for (int i = 0; i < channelsPerSide; i++)
        {
            byte[] data = [(byte)i];
            await clientChannels[i].WriteAsync(data, cts.Token);
            await serverChannels[i].WriteAsync(data, cts.Token);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RapidOpenClose_DataIntegrity()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int cycles = 200;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        for (int i = 0; i < cycles; i++)
        {
            var writeChannel = client.OpenChannel($"cycle-{i}");
            var readChannel = await server.AcceptChannelAsync($"cycle-{i}", cts.Token);
            await writeChannel.WaitForReadyAsync(cts.Token);

            byte[] sent = [(byte)(i % 256), (byte)((i * 7) % 256)];
            await writeChannel.WriteAsync(sent, cts.Token);

            var received = new byte[2];
            int totalRead = 0;
            while (totalRead < 2)
            {
                int r = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (r == 0) break;
                totalRead += r;
            }

            Assert.Equal(sent, received);
            await writeChannel.DisposeAsync();
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task LargePayload_SingleChannel_NoCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Send 2MB of data
        const int totalSize = 2 * 1024 * 1024;
        var sent = new byte[totalSize];
        Random.Shared.NextBytes(sent);
        var expectedHash = SHA256.HashData(sent);

        var writeChannel = client.OpenChannel("large");
        var readChannel = await server.AcceptChannelAsync("large", cts.Token);

        // Write in chunks
        var writeTask = Task.Run(async () =>
        {
            int offset = 0;
            while (offset < totalSize)
            {
                int chunk = Math.Min(16384, totalSize - offset);
                await writeChannel.WriteAsync(sent.AsMemory(offset, chunk), cts.Token);
                offset += chunk;
            }
            await writeChannel.DisposeAsync();
        }, cts.Token);

        var received = new byte[totalSize];
        int totalRead = 0;
        while (totalRead < totalSize)
        {
            int read = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (read > 0)
                totalRead += read;
            else
                await Task.Yield();
        }

        await writeTask;

        Assert.Equal(totalSize, totalRead);
        Assert.Equal(expectedHash, SHA256.HashData(received));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ExactFrameBoundaryWrites_NoDataLoss()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writeChannel = client.OpenChannel("boundaries");
        var readChannel = await server.AcceptChannelAsync("boundaries", cts.Token);

        // Write at various sizes: 1, 2, 4, 8, ..., 65536
        var allSent = new MemoryStream();
        var writeTask = Task.Run(async () =>
        {
            for (int size = 1; size <= 65536; size *= 2)
            {
                var data = new byte[size];
                Array.Fill(data, (byte)(size % 256));
                await writeChannel.WriteAsync(data, cts.Token);
                allSent.Write(data);
            }
            await writeChannel.DisposeAsync();
        }, cts.Token);

        var allReceived = new MemoryStream();
        var buffer = new byte[8192];
        int expectedTotal = Enumerable.Range(0, 17).Select(i => 1 << i).Sum(); // 131071
        while (allReceived.Length < expectedTotal)
        {
            int read = await readChannel.ReadAsync(buffer, cts.Token);
            if (read > 0)
                allReceived.Write(buffer, 0, read);
            else
                await Task.Yield();
        }

        await writeTask;

        Assert.Equal(allSent.ToArray(), allReceived.ToArray());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
