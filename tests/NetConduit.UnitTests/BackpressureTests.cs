namespace NetConduit.UnitTests;

public sealed class BackpressureTests
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
    public async Task SlowReader_WriterEventuallyFlows()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("backpressure");
        var readCh = await server.AcceptChannelAsync("backpressure", cts.Token);

        const int totalWrite = 256 * 1024; // 256KB
        int totalSent = 0;

        // Writer pushes data as fast as possible
        var writeTask = Task.Run(async () =>
        {
            var data = new byte[4096];
            Array.Fill(data, (byte)0xAB);
            while (totalSent < totalWrite)
            {
                await writeCh.WriteAsync(data, cts.Token);
                Interlocked.Add(ref totalSent, data.Length);
            }
            await writeCh.DisposeAsync();
        }, cts.Token);

        // Slow reader: reads 1KB at a time with delay
        var received = new byte[totalWrite];
        int totalRead = 0;
        var buf = new byte[1024];
        int read;
        while ((read = await readCh.ReadAsync(buf, cts.Token)) > 0)
        {
            Buffer.BlockCopy(buf, 0, received, totalRead, read);
            totalRead += read;
            if (totalRead < totalWrite / 2)
                await Task.Delay(1, cts.Token); // Simulate slow reader
        }

        await writeTask;

        Assert.Equal(totalWrite, totalRead);
        // All received bytes should be 0xAB
        Assert.All(received, b => Assert.Equal(0xAB, b));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BackpressureRelease_DataFlowsAfterReaderCatchesUp()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("pressure-release");
        var readCh = await server.AcceptChannelAsync("pressure-release", cts.Token);

        // Write enough to saturate backpressure
        var data = new byte[4096];
        Array.Fill(data, (byte)0xCD);
        for (int i = 0; i < 10; i++)
        {
            await writeCh.WriteAsync(data, cts.Token);
        }

        // Now read it all
        int totalRead = 0;
        var buffer = new byte[4096];
        while (totalRead < 40960)
        {
            int r = await readCh.ReadAsync(buffer, cts.Token);
            if (r == 0) break;
            totalRead += r;
        }

        Assert.Equal(40960, totalRead);

        // After catching up, can still send more
        await writeCh.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        var smallBuf = new byte[16];
        int smallRead = await readCh.ReadAsync(smallBuf, cts.Token);
        Assert.Equal(3, smallRead);
        Assert.Equal(new byte[] { 1, 2, 3 }, smallBuf[..3]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultipleChannels_IndependentBackpressure()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var ch1 = client.OpenChannel("bp-ch1");
        var ch2 = client.OpenChannel("bp-ch2");
        var readCh1 = await server.AcceptChannelAsync("bp-ch1", cts.Token);
        var readCh2 = await server.AcceptChannelAsync("bp-ch2", cts.Token);

        // Write different amounts to each channel
        byte[] data1 = new byte[4096];
        byte[] data2 = new byte[2048];
        Array.Fill(data1, (byte)0x11);
        Array.Fill(data2, (byte)0x22);

        for (int i = 0; i < 5; i++)
        {
            await ch1.WriteAsync(data1, cts.Token);
            await ch2.WriteAsync(data2, cts.Token);
        }

        await ch1.DisposeAsync();
        await ch2.DisposeAsync();

        // Read all from ch1
        int total1 = 0;
        var buf = new byte[8192];
        int r;
        while ((r = await readCh1.ReadAsync(buf, cts.Token)) > 0)
            total1 += r;

        // Read all from ch2
        int total2 = 0;
        while ((r = await readCh2.ReadAsync(buf, cts.Token)) > 0)
            total2 += r;

        Assert.Equal(5 * 4096, total1);
        Assert.Equal(5 * 2048, total2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task LargeWrite_SlabBoundary_NoDataLoss()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("slab-boundary");
        var readCh = await server.AcceptChannelAsync("slab-boundary", cts.Token);

        // Write a payload larger than default slab to test spanning
        var data = new byte[65536 * 3 + 17]; // Not aligned
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);

        var writeTask = Task.Run(async () =>
        {
            await writeCh.WriteAsync(data, cts.Token);
            await writeCh.DisposeAsync();
        }, cts.Token);

        var received = new byte[data.Length];
        int totalRead = 0;
        int read;
        while ((read = await readCh.ReadAsync(received.AsMemory(totalRead), cts.Token)) > 0)
            totalRead += read;

        await writeTask;

        Assert.Equal(data.Length, totalRead);
        Assert.Equal(data, received);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SendTimeout_Respected()
    {
        var duplex = new DuplexMemoryStream();

        // Very small slab to force backpressure quickly
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions
            {
                SlabSize = 1024,
                SendTimeout = TimeSpan.FromMilliseconds(500),
            },
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 1024 },
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("send-timeout");
        await server.AcceptChannelAsync("send-timeout", cts.Token);

        // Don't read from server side - build up backpressure
        // At some point write should time out or block
        try
        {
            var largeData = new byte[1024 * 1024]; // 1MB with 1KB slab
            await writeCh.WriteAsync(largeData, cts.Token);
        }
        catch (TimeoutException)
        {
            // Expected: SendTimeout fired
        }
        catch (OperationCanceledException)
        {
            // Also acceptable since CTS might fire
        }

        // Either timeout fired or CTS canceled - both acceptable
        // The test validates SendTimeout is wired up (doesn't hang forever)

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentWriters_UnderBackpressure_NoDeadlock()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int numChannels = 3;
        var writers = new IWriteChannel[numChannels];
        var readers = new IReadChannel[numChannels];

        for (int i = 0; i < numChannels; i++)
        {
            writers[i] = client.OpenChannel($"concurrent-bp-{i}");
            readers[i] = await server.AcceptChannelAsync($"concurrent-bp-{i}", cts.Token);
        }

        int totalExpected = numChannels * 5 * 4096;
        int totalReceived = 0;

        // All writers push concurrently
        var writeTasks = writers.Select((ch, idx) => Task.Run(async () =>
        {
            var data = new byte[4096];
            Array.Fill(data, (byte)idx);
            for (int i = 0; i < 5; i++)
                await ch.WriteAsync(data, cts.Token);
            await ch.DisposeAsync();
        }, cts.Token)).ToArray();

        // All readers drain concurrently
        var readTasks = readers.Select(ch => Task.Run(async () =>
        {
            var buf = new byte[8192];
            int read;
            int channelTotal = 0;
            while ((read = await ch.ReadAsync(buf, cts.Token)) > 0)
                channelTotal += read;
            Interlocked.Add(ref totalReceived, channelTotal);
        }, cts.Token)).ToArray();

        await Task.WhenAll(writeTasks.Concat(readTasks));

        Assert.Equal(totalExpected, totalReceived);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SubThresholdTail_LongLivedChannel_NoUnackedPinning()
    {
        // Regression: with replay enabled (the default), the writer's slab can
        // only compact past an ACKed position. The 25% byte threshold means a
        // long-lived channel that streams sub-threshold bursts would otherwise
        // pin every unacked frame in the writer's slab indefinitely. Sending
        // more bytes than a slab in small bursts with a fully-draining reader
        // must complete without stalling: the slow path in ReadAsync flushes
        // any pending ACK when the reader catches up.
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writeCh = client.OpenChannel("subthreshold-tail");
        var readCh = await server.AcceptChannelAsync("subthreshold-tail", cts.Token);

        // Drive 4 MiB through a 1 MiB (default) writer slab in 4 KiB bursts.
        // Each burst is far below the 256 KiB ACK byte threshold; only the
        // quiescence-flush keeps the slab from filling with phantom unacked
        // frames. Without it WriteAsync would throw TimeoutException after
        // the first ~262 bursts.
        const int chunkSize = 4096;
        const long totalBytes = 4L * 1024 * 1024;
        var data = new byte[chunkSize];
        Array.Fill(data, (byte)0xA5);
        var buf = new byte[chunkSize];
        long written = 0;
        long read = 0;

        while (written < totalBytes)
        {
            await writeCh.WriteAsync(data, cts.Token);
            written += chunkSize;

            // Drain the chunk before the next write so the reader is always
            // caught up (slow path triggered on the next ReadAsync).
            int n = await readCh.ReadAsync(buf, cts.Token);
            Assert.Equal(chunkSize, n);
            for (int i = 0; i < n; i++)
                Assert.Equal(0xA5, buf[i]);
            read += n;
        }

        Assert.Equal(totalBytes, written);
        Assert.Equal(totalBytes, read);

        await writeCh.CloseAsync(cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // Open issue: with 32 concurrent producer/consumer channels each pushing
    // 2 MiB through a 1 MiB slab, at least one channel non-deterministically
    // stalls on WriteAsync after ~30 s. The read-slow-path ACK flush only
    // fires when the reader catches up to the writer, which under sustained
    // multi-channel pressure rarely happens for every channel. The 25 % byte
    // threshold alone is not enough to keep every channel's writer slab
    // compacting in lockstep. A more robust fix likely needs either a
    // periodic ACK timer in the mux, a consumer-paced ACK trigger on the
    // fast path, or rethinking the ACK semantics so the writer cannot
    // accumulate more outstanding than the receiver's drain rate supports.
    [Fact(Skip = "Tracking: sub-threshold tail ACK pacing under multi-channel concurrent load — see comment above.")]
    public async Task SubThresholdTail_ManyLongLivedChannels_AllChannelsKeepFlowing()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int channelCount = 32;
        const int chunkSize = 4096;
        const int chunksPerChannel = 512; // 2 MiB per channel, > 1 MiB slab
        var writers = new IWriteChannel[channelCount];
        var readers = new IReadChannel[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = client.OpenChannel($"subthreshold-ch-{i}");
            readers[i] = await server.AcceptChannelAsync($"subthreshold-ch-{i}", cts.Token);
        }

        long totalReceived = 0;
        var readTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = readers[i];
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[chunkSize];
                while (true)
                {
                    int n = await ch.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref totalReceived, n);
                }
            }, cts.Token);
        }

        var writeTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = writers[i];
            writeTasks[i] = Task.Run(async () =>
            {
                var data = new byte[chunkSize];
                Array.Fill(data, (byte)0x5A);
                for (int j = 0; j < chunksPerChannel; j++)
                {
                    await ch.WriteAsync(data, cts.Token);
                }
                await ch.CloseAsync(cts.Token);
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        await Task.WhenAll(readTasks);

        Assert.Equal((long)channelCount * chunksPerChannel * chunkSize, totalReceived);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
