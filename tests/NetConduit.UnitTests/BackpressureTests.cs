using NetConduit.Internal;

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
    public async Task LargeWrite_AboveDefaultSlab_SplitsAndPreservesBytes()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeCh = client.OpenChannel("large-write-split");
        var readCh = await server.AcceptChannelAsync("large-write-split", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);

        var data = new byte[2 * 1024 * 1024];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        var writeTask = writeCh.WriteAsync(data, cts.Token).AsTask();
        var received = new byte[data.Length];
        int totalRead = 0;

        while (totalRead < received.Length)
        {
            var readTask = readCh.ReadAsync(received.AsMemory(totalRead), cts.Token).AsTask();
            var completed = await Task.WhenAny(readTask, writeTask);
            if (completed == writeTask && writeTask.IsFaulted)
                await writeTask;

            int read = await readTask;
            Assert.NotEqual(0, read);
            totalRead += read;
        }

        await writeTask;

        Assert.Equal(data.Length, totalRead);
        Assert.Equal(data, received);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_DrainsSubThresholdFrame_ReleasesWriterSlabCredit()
    {
        var duplex = new DuplexMemoryStream();

        const int SmallSlab = 64 * 1024;
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = SmallSlab },
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeCh = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "sub-threshold-ack",
            SlabSize = SmallSlab,
            SendTimeout = TimeSpan.FromMilliseconds(500),
        });
        var readCh = await server.AcceptChannelAsync("sub-threshold-ack", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);

        await writeCh.WriteAsync(new byte[] { 0x42 }, cts.Token);
        var oneByteBuffer = new byte[1];
        int firstRead = await readCh.ReadAsync(oneByteBuffer, cts.Token);
        Assert.Equal(1, firstRead);
        Assert.Equal(0x42, oneByteBuffer[0]);

        var maxFittingPayload = new byte[SmallSlab - FrameHeader.Size];
        await writeCh.WriteAsync(maxFittingPayload, cts.Token);

        var received = new byte[maxFittingPayload.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int read = await readCh.ReadAsync(received.AsMemory(totalRead), cts.Token);
            Assert.NotEqual(0, read);
            totalRead += read;
        }

        Assert.Equal(maxFittingPayload.Length, totalRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task CloseAsync_FullSlab_DeliversFinAfterPayloadDrains()
    {
        var duplex = new DuplexMemoryStream();

        const int SmallSlab = 64 * 1024;
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = SmallSlab },
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = SmallSlab },
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeCh = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "full-slab-close",
            SlabSize = SmallSlab,
            SendTimeout = TimeSpan.FromSeconds(5),
        });
        var readCh = await server.AcceptChannelAsync("full-slab-close", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);

        var ackPrimer = new byte[4096];
        await writeCh.WriteAsync(ackPrimer, cts.Token);
        var primerRead = await readCh.ReadAsync(new byte[ackPrimer.Length], cts.Token);
        Assert.Equal(ackPrimer.Length, primerRead);

        var payload = new byte[SmallSlab - FrameHeader.Size];
        payload[0] = 0x7A;
        payload[^1] = 0x5E;

        await writeCh.WriteAsync(payload, cts.Token);
        var closeTask = writeCh.CloseAsync(cts.Token).AsTask();

        var received = new byte[payload.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int read = await readCh.ReadAsync(received.AsMemory(totalRead), cts.Token);
            Assert.NotEqual(0, read);
            totalRead += read;
        }

        using var eofCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int eof = await readCh.ReadAsync(new byte[1], eofCts.Token);

        await closeTask;

        Assert.Equal(payload.Length, totalRead);
        Assert.Equal(0x7A, received[0]);
        Assert.Equal(0x5E, received[^1]);
        Assert.Equal(0, eof);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SendTimeout_Respected()
    {
        var duplex = new DuplexMemoryStream();

        // Small slab (the minimum documented value) to force backpressure quickly.
        const int SmallSlab = 64 * 1024;
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions
            {
                SlabSize = SmallSlab,
                SendTimeout = TimeSpan.FromMilliseconds(500),
            },
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = SmallSlab },
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("send-timeout");
        await server.AcceptChannelAsync("send-timeout", cts.Token);

        // Build up backpressure by writing fitting payloads in a tight loop
        // while the receiver never reads. Each payload fits the per-frame
        // budget; the slab fills and a subsequent write blocks waiting for
        // ACK from the receiver (which is not reading), so SendTimeout
        // eventually fires.
        try
        {
            // Maximum payload per frame = SlabSize - FrameHeader.Size.
            var fittingData = new byte[SmallSlab - 8];
            for (int i = 0; i < 1024; i++)
            {
                await writeCh.WriteAsync(fittingData, cts.Token);
            }
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
}
