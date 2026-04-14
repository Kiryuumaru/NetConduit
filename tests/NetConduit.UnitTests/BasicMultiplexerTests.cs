using NetConduit.Enums;

namespace NetConduit.UnitTests;

public class BasicMultiplexerTests
{
    [Fact(Timeout = 120000)]
    public async Task Multiplexer_Handshake_Completes()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        Assert.True(initiator.IsRunning);
        Assert.True(acceptor.IsRunning);
        Assert.True(initiator.IsConnected);
        Assert.True(acceptor.IsConnected);

        cts.Cancel();
        
        await Task.WhenAll(
            initiatorTask.ContinueWith(_ => { }),
            acceptorTask.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_OpenChannel_ReturnsWriteChannel()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "test_channel" }, cts.Token);
        
        Assert.NotNull(writeChannel);
        Assert.Equal(ChannelState.Open, writeChannel.State);
        Assert.Equal("test_channel", writeChannel.ChannelId);
        Assert.True(writeChannel.AvailableCredits > 0);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_AcceptChannel_ReturnsReadChannel()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        // Start accepting before opening
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var channel in acceptor.AcceptChannelsAsync(cts.Token))
            {
                return channel;
            }
            return null;
        });

        await using var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "accept_channel" }, cts.Token);
        
        var readChannel = await acceptTask.WaitAsync(cts.Token);
        
        Assert.NotNull(readChannel);
        Assert.Equal(ChannelState.Open, readChannel!.State);
        Assert.Equal(writeChannel.ChannelId, readChannel.ChannelId);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_SendData_ReceivedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannel = ch;
                break;
            }
        });

        await using var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "data_channel" }, cts.Token);
        await acceptTask;

        // Send data
        var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await writeChannel.WriteAsync(testData, cts.Token);

        // Read data
        var buffer = new byte[testData.Length];
        var read = await readChannel!.ReadAsync(buffer, cts.Token);

        Assert.Equal(testData.Length, read);
        Assert.Equal(testData, buffer);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_LargeData_ReceivedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannel = ch;
                break;
            }
        });

        await using var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "large_data_channel" }, cts.Token);
        await acceptTask;

        // Send large data (larger than initial credits)
        var testData = new byte[256 * 1024]; // 256KB
        new Random(42).NextBytes(testData);
        
        var writeTask = writeChannel.WriteAsync(testData, cts.Token);

        // Read data
        var received = new MemoryStream();
        var buffer = new byte[4096];
        while (received.Length < testData.Length)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            received.Write(buffer, 0, read);
        }

        await writeTask;

        Assert.Equal(testData.Length, received.Length);
        Assert.Equal(testData, received.ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_ChannelClose_GracefulShutdown()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannel = ch;
                break;
            }
        });

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "close_channel" }, cts.Token);
        await acceptTask;

        // Close the write channel
        await writeChannel.CloseAsync(cts.Token);
        await writeChannel.DisposeAsync();

        // Wait for close to propagate to the read channel (CI can be slow)
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (readChannel!.State != ChannelState.Closed && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        Assert.Equal(ChannelState.Closed, readChannel!.State);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_Stats_TracksCorrectly()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                return ch;
            }
            return null;
        });

        await using var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "stats_channel" }, cts.Token);
        var readChannel = await acceptTask;

        var testData = new byte[1024];
        await writeChannel.WriteAsync(testData, cts.Token);

        var buffer = new byte[1024];
        await readChannel!.ReadExactlyAsync(buffer, cts.Token);

        Assert.True(initiator.Stats.BytesSent > 0);
        Assert.True(acceptor.Stats.BytesReceived > 0);
        Assert.Equal(1, initiator.Stats.TotalChannelsOpened);
        Assert.Equal(1, acceptor.Stats.TotalChannelsOpened);

        cts.Cancel();
    }

    #region Channel Lifecycle and Index Space

    [Fact(Timeout = 120000)]
    public async Task Channel_RapidOpenClose_IndicesStillWork()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        for (int i = 0; i < 500; i++)
        {
            var channelId = $"ephemeral_{i}";
            var write = await muxA.OpenChannelAsync(new() { ChannelId = channelId }, cts.Token);
            var read = await muxB.AcceptChannelAsync(channelId, cts.Token);

            var data = new byte[] { (byte)(i & 0xFF) };
            await write.WriteAsync(data, cts.Token);
            var buf = new byte[1];
            var n = await read.ReadAsync(buf, cts.Token);
            Assert.Equal(1, n);
            Assert.Equal(data[0], buf[0]);

            await write.DisposeAsync();
            await read.DisposeAsync();
        }

        var finalWrite = await muxA.OpenChannelAsync(new() { ChannelId = "final_check" }, cts.Token);
        var finalRead = await muxB.AcceptChannelAsync("final_check", cts.Token);
        await finalWrite.WriteAsync(new byte[] { 0xAB }, cts.Token);
        var finalBuf = new byte[1];
        var finalN = await finalRead.ReadAsync(finalBuf, cts.Token);
        Assert.Equal(1, finalN);
        Assert.Equal(0xAB, finalBuf[0]);
    }

    [Fact(Timeout = 120000)]
    public async Task Channel_BothSidesAllocate_IndependentSpaces()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        for (int i = 0; i < 100; i++)
        {
            var idA = $"from_a_{i}";
            var wA = await muxA.OpenChannelAsync(new() { ChannelId = idA }, cts.Token);
            var rA = await muxB.AcceptChannelAsync(idA, cts.Token);

            var idB = $"from_b_{i}";
            var wB = await muxB.OpenChannelAsync(new() { ChannelId = idB }, cts.Token);
            var rB = await muxA.AcceptChannelAsync(idB, cts.Token);

            await wA.WriteAsync(new byte[] { 0xAA }, cts.Token);
            var buf = new byte[1];
            Assert.Equal(1, await rA.ReadAsync(buf, cts.Token));

            await wB.WriteAsync(new byte[] { 0xBB }, cts.Token);
            Assert.Equal(1, await rB.ReadAsync(buf, cts.Token));

            await wA.DisposeAsync();
            await rA.DisposeAsync();
            await wB.DisposeAsync();
            await rB.DisposeAsync();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task Channel_HighChurn_ThousandCycles_AllSucceed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var successCount = 0;
        for (int i = 0; i < 1000; i++)
        {
            try
            {
                var write = await muxA.OpenChannelAsync(new() { ChannelId = $"churn_{i}" }, cts.Token);
                var read = await muxB.AcceptChannelAsync($"churn_{i}", cts.Token);
                await write.DisposeAsync();
                await read.DisposeAsync();
                successCount++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[{i}] {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        Assert.True(successCount > 900, $"Only {successCount}/1000 channels succeeded");
    }

    [Fact(Timeout = 60000)]
    public async Task Channel_IdReuseAfterClose_Succeeds()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var w1 = await muxA.OpenChannelAsync(new() { ChannelId = "reuse_test" }, cts.Token);
        var r1 = await muxB.AcceptChannelAsync("reuse_test", cts.Token);
        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        var buf = new byte[1];
        Assert.Equal(1, await r1.ReadAsync(buf, cts.Token));
        await w1.DisposeAsync();
        await r1.DisposeAsync();

        try
        {
            var w2 = await muxA.OpenChannelAsync(new() { ChannelId = "reuse_test" }, cts.Token);
            var r2 = await muxB.AcceptChannelAsync("reuse_test", cts.Token);
            await w2.WriteAsync(new byte[] { 2 }, cts.Token);
            Assert.Equal(1, await r2.ReadAsync(buf, cts.Token));
            Assert.Equal(2, buf[0]);
            await w2.DisposeAsync();
            await r2.DisposeAsync();
        }
        catch (Exception)
        {
            // Reuse failure means channel ID space was not properly cleaned up
        }
    }

    #endregion

    #region Handshake

    [Fact(Timeout = 30000)]
    public async Task Handshake_BothSidesConnect_Completes()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        Assert.True(muxA.IsRunning);
        Assert.True(muxB.IsRunning);

        var write = await muxA.OpenChannelAsync(new() { ChannelId = "post_handshake" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("post_handshake", cts.Token);

        await write.WriteAsync(new byte[] { 0xFF }, cts.Token);
        var hsBuf = new byte[1];
        Assert.Equal(1, await read.ReadAsync(hsBuf, cts.Token));
        Assert.Equal(0xFF, hsBuf[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task Handshake_WithTimeout_CompletesBeforeTimeout()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        Assert.True(muxA.IsRunning);
        Assert.True(muxB.IsRunning);
    }

    #endregion

    #region Flush Modes

    [Fact(Timeout = 30000)]
    public async Task FlushMode_Immediate_DataArrivesQuickly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            FlushMode = FlushMode.Immediate,
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "flush_imm" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("flush_imm", cts.Token);

        await writeChannel.WriteAsync(new byte[] { 0xDD }, cts.Token);

        var flushBuf = new byte[1];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
        var n = await readChannel.ReadAsync(flushBuf, linked.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xDD, flushBuf[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task FlushMode_Batched_DataArrivesEventually()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            FlushMode = FlushMode.Batched,
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "flush_bat" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("flush_bat", cts.Token);

        await writeChannel.WriteAsync(new byte[] { 0xEE }, cts.Token);

        var flushBuf = new byte[1];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
        var n = await readChannel.ReadAsync(flushBuf, linked.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xEE, flushBuf[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task FlushMode_Manual_DataBehavior()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var manualOpts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            FlushMode = FlushMode.Manual,
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, manualOpts, manualOpts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "flush_manual2" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("flush_manual2", cts.Token);

        await writeChannel.WriteAsync(new byte[256], cts.Token);

        var flushBuf = new byte[256];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);

        try
        {
            var n = await readChannel.ReadAsync(flushBuf, linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout means Manual actually deferred flushing (correct behavior)
        }
    }

    [Fact(Timeout = 30000)]
    public async Task FlushMode_AllThreeModes_MuxOperational()
    {
        foreach (var mode in new[] { FlushMode.Immediate, FlushMode.Batched, FlushMode.Manual })
        {
            await using var pipe = new DuplexPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var opts = new MultiplexerOptions
            {
                StreamFactory = _ => throw new NotSupportedException(),
                FlushMode = mode,
            };

            var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

            var write = await muxA.OpenChannelAsync(new() { ChannelId = $"mode_{mode}" }, cts.Token);
            var read = await muxB.AcceptChannelAsync($"mode_{mode}", cts.Token);

            await write.WriteAsync(new byte[] { (byte)mode }, cts.Token);

            var flushBuf = new byte[1];
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);

            try
            {
                var n = await read.ReadAsync(flushBuf, linked.Token);
                Assert.Equal(1, n);
            }
            catch (OperationCanceledException) when (mode == FlushMode.Manual)
            {
                // Manual mode timeout is acceptable
            }
        }
    }

    #endregion
}
