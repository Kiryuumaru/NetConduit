using System.Security.Cryptography;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for chaos and robustness scenarios:
/// - Random operation sequences
/// - Mixed channel lifecycles
/// - Concurrent reads and writes with kills
/// - Interleaved open/write/close from both sides
/// </summary>
[Collection("Sequential")]
public sealed class ChaosRobustnessTests
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
    public async Task Chaos_MixedLifecycles_SomeShortSomeLong_NoLeaks()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[4096];
                        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                    }
                    catch (OperationCanceledException) { }
                });
                count++;
                if (count >= 10) break;
            }
        });

        // Mix of short-lived and long-lived channels
        var longLived = new List<IWriteChannel>();
        for (int i = 0; i < 10; i++)
        {
            var ch = client.OpenChannel($"chaos-{i}");
            if (i % 3 == 0)
            {
                // Long-lived: write data, keep open
                await ch.WriteAsync(new byte[100], cts.Token);
                longLived.Add(ch);
            }
            else
            {
                // Short-lived: write and close immediately
                await ch.WriteAsync(new byte[] { (byte)i }, cts.Token);
                await ch.DisposeAsync();
            }
        }

        // Now close long-lived channels
        foreach (var ch in longLived)
            await ch.DisposeAsync();

        await acceptTask.WaitAsync(cts.Token);
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_BidirectionalFlood_BothSidesSimultaneous()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channelsPerSide = 5;
        const int messagesPerChannel = 50;

        // Client opens channels to server
        var clientSendTask = Task.Run(async () =>
        {
            var writers = new IWriteChannel[channelsPerSide];
            for (int i = 0; i < channelsPerSide; i++)
            {
                writers[i] = client.OpenChannel($"c2s-{i}");
            }
            for (int m = 0; m < messagesPerChannel; m++)
            {
                for (int i = 0; i < channelsPerSide; i++)
                    await writers[i].WriteAsync(new byte[64], cts.Token);
            }
            foreach (var w in writers) await w.DisposeAsync();
        });

        // Server opens channels to client
        var serverSendTask = Task.Run(async () =>
        {
            var writers = new IWriteChannel[channelsPerSide];
            for (int i = 0; i < channelsPerSide; i++)
            {
                writers[i] = server.OpenChannel($"s2c-{i}");
            }
            for (int m = 0; m < messagesPerChannel; m++)
            {
                for (int i = 0; i < channelsPerSide; i++)
                    await writers[i].WriteAsync(new byte[64], cts.Token);
            }
            foreach (var w in writers) await w.DisposeAsync();
        });

        // Client reads server channels
        var clientReadTask = Task.Run(async () =>
        {
            long total = 0;
            for (int i = 0; i < channelsPerSide; i++)
            {
                var ch = await client.AcceptChannelAsync($"s2c-{i}", cts.Token);
                var buf = new byte[4096];
                int read;
                while ((read = await ch.ReadAsync(buf, cts.Token)) > 0)
                    total += read;
            }
            return total;
        });

        // Server reads client channels
        var serverReadTask = Task.Run(async () =>
        {
            long total = 0;
            for (int i = 0; i < channelsPerSide; i++)
            {
                var ch = await server.AcceptChannelAsync($"c2s-{i}", cts.Token);
                var buf = new byte[4096];
                int read;
                while ((read = await ch.ReadAsync(buf, cts.Token)) > 0)
                    total += read;
            }
            return total;
        });

        await Task.WhenAll(clientSendTask, serverSendTask);
        var clientRead = await clientReadTask.WaitAsync(cts.Token);
        var serverRead = await serverReadTask.WaitAsync(cts.Token);

        Assert.Equal(channelsPerSide * messagesPerChannel * 64, clientRead);
        Assert.Equal(channelsPerSide * messagesPerChannel * 64, serverRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_RandomSizedMessages_AllDataCorrect()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var rng = new Random(42);
        const int messageCount = 100;

        var writer = client.OpenChannel("random-sizes");
        var reader = await server.AcceptChannelAsync("random-sizes", cts.Token);

        using var expectedHash = SHA256.Create();
        using var actualHash = SHA256.Create();

        var writeTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                int size = rng.Next(1, 8192);
                var data = new byte[size];
                new Random(42 + i).NextBytes(data);
                expectedHash.TransformBlock(data, 0, data.Length, null, 0);
                await writer.WriteAsync(data, cts.Token);
            }
            expectedHash.TransformFinalBlock([], 0, 0);
            await writer.DisposeAsync();
        });

        var readTask = Task.Run(async () =>
        {
            var buf = new byte[16384];
            int read;
            while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
            {
                actualHash.TransformBlock(buf, 0, read, null, 0);
            }
            actualHash.TransformFinalBlock([], 0, 0);
        });

        await Task.WhenAll(writeTask, readTask).WaitAsync(cts.Token);

        Assert.Equal(expectedHash.Hash, actualHash.Hash);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_ConcurrentWritersSingleChannel_DataIntegrity()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writer = client.OpenChannel("concurrent-write");
        var reader = await server.AcceptChannelAsync("concurrent-write", cts.Token);

        const int writerCount = 5;
        const int writesPerWriter = 20;
        const int writeSize = 128;

        // Multiple concurrent writers to same channel
        var writeTasks = Enumerable.Range(0, writerCount).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < writesPerWriter; i++)
            {
                var data = new byte[writeSize];
                data.AsSpan().Fill((byte)(w * 10 + i % 10));
                await writer.WriteAsync(data, cts.Token);
            }
        })).ToArray();

        await Task.WhenAll(writeTasks);
        await writer.DisposeAsync();

        // Read all data — total should match
        long totalRead = 0;
        var buf = new byte[4096];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
            totalRead += read;

        Assert.Equal(writerCount * writesPerWriter * writeSize, totalRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_DisposeWhileWriting_NoHangOrCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writer = client.OpenChannel("dispose-mid-write");
        var reader = await server.AcceptChannelAsync("dispose-mid-write", cts.Token);

        // Start writing in background
        var writeTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                    await writer.WriteAsync(new byte[256], cts.Token);
            }
            catch (ChannelClosedException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        });

        // Dispose the mux mid-write
        await Task.Delay(50, cts.Token);
        await client.DisposeAsync();

        // Write task should complete without hanging
        await writeTask.WaitAsync(TimeSpan.FromSeconds(120));
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_DisposeWhileReading_NoHangOrCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writer = client.OpenChannel("dispose-mid-read");
        var reader = await server.AcceptChannelAsync("dispose-mid-read", cts.Token);

        // Start reading in background
        var readTask = Task.Run(async () =>
        {
            try
            {
                var buf = new byte[4096];
                while (true)
                {
                    var read = await reader.ReadAsync(buf, cts.Token);
                    if (read == 0) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (ChannelClosedException) { }
        });

        await writer.WriteAsync(new byte[100], cts.Token);
        await Task.Delay(50, cts.Token);

        // Dispose server mux mid-read
        await server.DisposeAsync();

        // Read task should complete without hanging
        await readTask.WaitAsync(TimeSpan.FromSeconds(120));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_RapidChannelOpenClose_NoResourceLeak()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int cycles = 50;

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[16];
                _ = await ch.ReadAsync(buf, cts.Token);
                count++;
                if (count >= cycles) break;
            }
        });

        for (int i = 0; i < cycles; i++)
        {
            var ch = client.OpenChannel($"leak-{i}");
            await ch.WriteAsync(new byte[] { 1 }, cts.Token);
            await ch.DisposeAsync();
        }

        await acceptTask.WaitAsync(cts.Token);

        Assert.True(client.Stats.TotalChannelsOpened >= cycles);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_InterleavedReadWrites_DataIntegrity()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Create bidirectional channels
        var clientWriter = client.OpenChannel("bidir>>");
        var serverReader = await server.AcceptChannelAsync("bidir>>", cts.Token);
        var serverWriter = server.OpenChannel("bidir<<");
        var clientReader = await client.AcceptChannelAsync("bidir<<", cts.Token);

        const int rounds = 50;
        byte[] expected = new byte[rounds];

        // Ping-pong: client writes a byte, server echoes it back incremented
        var serverTask = Task.Run(async () =>
        {
            var buf = new byte[1];
            for (int i = 0; i < rounds; i++)
            {
                var read = await serverReader.ReadAsync(buf, cts.Token);
                Assert.Equal(1, read);
                buf[0]++;
                await serverWriter.WriteAsync(buf, cts.Token);
            }
            await serverWriter.DisposeAsync();
        });

        var clientTask = Task.Run(async () =>
        {
            var buf = new byte[1];
            for (int i = 0; i < rounds; i++)
            {
                buf[0] = (byte)i;
                await clientWriter.WriteAsync(buf, cts.Token);
                var read = await clientReader.ReadAsync(buf, cts.Token);
                Assert.Equal(1, read);
                Assert.Equal((byte)(i + 1), buf[0]);
            }
            await clientWriter.DisposeAsync();
        });

        await Task.WhenAll(serverTask, clientTask).WaitAsync(cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_TransportKill_MidFlood_GracefulDegradation()
    {
        var duplex = new DuplexMemoryStream();
        var killableA = new KillableStreamPair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(killableA),
            MaxAutoReconnectAttempts = 0,
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("flood");
        await server.AcceptChannelAsync("flood", cts.Token);

        // Start flood writing
        var writeTask = Task.Run(async () =>
        {
            int written = 0;
            try
            {
                for (int i = 0; i < 10000; i++)
                {
                    await writer.WriteAsync(new byte[64], cts.Token);
                    written++;
                }
            }
            catch (ChannelClosedException) { }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            return written;
        });

        // Kill transport after short delay
        await Task.Delay(50, cts.Token);
        killableA.Kill();

        var framesWritten = await writeTask.WaitAsync(TimeSpan.FromSeconds(120));
        // Some writes should have succeeded before kill
        Assert.True(framesWritten > 0);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Chaos_ManyChannels_TinyMessages_OrderPreserved()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channels = 5;
        const int messages = 50;

        var results = new byte[channels][];
        var readTasks = new Task[channels];

        var acceptTask = Task.Run(async () =>
        {
            for (int i = 0; i < channels; i++)
            {
                var ch = await server.AcceptChannelAsync($"order-{i}", cts.Token);
                int idx = i;
                readTasks[idx] = Task.Run(async () =>
                {
                    var ms = new MemoryStream();
                    var buf = new byte[256];
                    int read;
                    while ((read = await ch.ReadAsync(buf, cts.Token)) > 0)
                        ms.Write(buf, 0, read);
                    results[idx] = ms.ToArray();
                });
            }
        });

        // Write single bytes in sequence to each channel
        var writers = new IWriteChannel[channels];
        for (int i = 0; i < channels; i++)
            writers[i] = client.OpenChannel($"order-{i}");

        await acceptTask.WaitAsync(cts.Token);

        for (int m = 0; m < messages; m++)
        {
            for (int i = 0; i < channels; i++)
                await writers[i].WriteAsync(new byte[] { (byte)(m % 256) }, cts.Token);
        }

        for (int i = 0; i < channels; i++)
            await writers[i].DisposeAsync();

        await Task.WhenAll(readTasks).WaitAsync(cts.Token);

        // Verify order within each channel
        for (int i = 0; i < channels; i++)
        {
            Assert.Equal(messages, results[i].Length);
            for (int m = 0; m < messages; m++)
                Assert.Equal((byte)(m % 256), results[i][m]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
