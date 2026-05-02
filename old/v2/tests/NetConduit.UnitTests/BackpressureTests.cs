using System.Collections.Concurrent;
using NetConduit.Internal;
using NetConduit.Models;

namespace NetConduit.UnitTests;

[Collection("HighMemory")]
[Trait("Category", "HighMemory")]
public class BackpressureTests
{
    [Fact(Timeout = 120000)]
    public async Task Backpressure_SlowReader_SenderBlocks()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
        var acceptorOptions = new MultiplexerOptions 
        { 
             StreamFactory = _ => null!, DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 1024, MaxCredits = 1024 }
        };
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
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

        var writeChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "backpressure_channel", SendTimeout = TimeSpan.FromSeconds(30) },
            cts.Token);
        await acceptTask;

        // Send more data than credits allow
        var largeData = new byte[4096]; // 4x the credits
        Random.Shared.NextBytes(largeData);
        var writeStarted = DateTime.UtcNow;
        
        var writeTask = Task.Run(async () =>
        {
            await writeChannel.WriteAsync(largeData, cts.Token);
        });

        // Don't read - sender should block after sending initial credits worth of data
        await Task.Delay(500);
        
        // At this point, the write should have sent 1024 bytes and be waiting for more credits
        Assert.False(writeTask.IsCompleted, "Write should be blocked waiting for credits");

        // Read data in multiple chunks and verify credits are granted
        var totalRead = 0;
        var received = new byte[4096];
        var buffer = new byte[512]; // Read in chunks
        while (totalRead < 4096)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            Buffer.BlockCopy(buffer, 0, received, totalRead, read);
            totalRead += read;
        }

        // Write should eventually complete
        await writeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(writeTask.IsCompleted);
        Assert.Equal(largeData, received);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Backpressure_Timeout_ThrowsException()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
        var acceptorOptions = new MultiplexerOptions 
        { 
             StreamFactory = _ => null!, DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 100, MaxCredits = 100 }
        };
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.Start(cts.Token);
        var acceptorTask = acceptor.Start(cts.Token);

        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                // Don't read - just accept
                break;
            }
        });

        var writeChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "timeout_channel", SendTimeout = TimeSpan.FromMilliseconds(500) }, // Short timeout
            cts.Token);
        await acceptTask;

        // Send more data than credits allow
        var largeData = new byte[1000];

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await writeChannel.WriteAsync(largeData, cts.Token);
        });

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Backpressure_CreditsAutoGrant_AfterRead()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits to test auto-grant behavior
        var acceptorOptions = new MultiplexerOptions 
        { 
             StreamFactory = _ => null!, DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 1024, MaxCredits = 1024 }
        };
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
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

        var writeChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "autogrant_channel" },
            cts.Token);
        await acceptTask;

        var creditsBeforeRead = writeChannel.Stats.CreditsGranted;

        // Send data to consume credits
        var sentData = new byte[512];
        Random.Shared.NextBytes(sentData);
        await writeChannel.WriteAsync(sentData, cts.Token);
        
        // Read data to trigger auto-grant
        var buffer = new byte[512];
        await readChannel!.ReadExactlyAsync(buffer, cts.Token);

        Assert.Equal(sentData, buffer);

        await Task.Delay(100); // Wait for credit grant to arrive

        // Credits should have been granted after reading consumed data
        var creditsAfterRead = writeChannel.Stats.CreditsGranted;
        Assert.True(creditsAfterRead > creditsBeforeRead, 
            $"Expected credits to be granted after read. Before: {creditsBeforeRead}, After: {creditsAfterRead}");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Backpressure_InfiniteTimeout_WaitsForever()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
        var acceptorOptions = new MultiplexerOptions 
        { 
             StreamFactory = _ => null!, DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 100, MaxCredits = 100 }
        };
        
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        
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

        var writeChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "infinite_timeout_channel", SendTimeout = Timeout.InfiniteTimeSpan }, // Never timeout
            cts.Token);
        await acceptTask;

        var sentData = new byte[1000];
        Random.Shared.NextBytes(sentData);

        var writeTask = Task.Run(async () =>
        {
            await writeChannel.WriteAsync(sentData, cts.Token);
        });

        // Wait a bit - should still be waiting (only 100 initial credits, but writing 1000 bytes)
        await Task.Delay(500);
        Assert.False(writeTask.IsCompleted, "Write should still be waiting with infinite timeout");

        // Read to unblock - read in small chunks to trigger credit grants
        var totalRead = 0;
        var received = new byte[1000];
        var buffer = new byte[100];
        while (totalRead < 1000)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            Buffer.BlockCopy(buffer, 0, received, totalRead, read);
            totalRead += read;
        }

        // Wait for write to complete after reading all data
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(sentData, received);

        // Cancel to cleanup
        cts.Cancel();
    }

    #region WriteChannel Credit Accounting

    [Fact(Timeout = 60000)]
    public async Task WriteChannel_ConcurrentWrites_CreditsNeverGoNegative()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("credit_test", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("credit_test", cts.Token);

        var drainTask = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var n = await readChannel.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });

        var errors = new ConcurrentBag<Exception>();
        var totalBytesSent = 0L;
        var tasks = new Task[8];

        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                var data = new byte[4096];
                Random.Shared.NextBytes(data);
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        await writeChannel.WriteAsync(data, cts.Token);
                        Interlocked.Add(ref totalBytesSent, data.Length);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                        break;
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        Assert.Empty(errors);
        Assert.True(totalBytesSent > 0, "Should have sent some data");
    }

    [Fact(Timeout = 60000)]
    public async Task WriteChannel_SingleByteWrites_ManyTimes_Succeeds()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("credit_1byte", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("credit_1byte", cts.Token);

        const int count = 5000;
        var readTask = Task.Run(async () =>
        {
            var received = new byte[count];
            var totalRead = 0;
            while (totalRead < count)
            {
                var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }
            return received;
        });

        for (int i = 0; i < count; i++)
        {
            await writeChannel.WriteAsync(new byte[] { (byte)(i & 0xFF) }, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);

        var result = await readTask;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((byte)(i & 0xFF), result[i]);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task WriteChannel_ExactCreditBoundary_NoUnderflow()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 1024, MaxCredits = 1024 },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("credit_exact", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("credit_exact", cts.Token);

        var data = new byte[1024];
        Random.Shared.NextBytes(data);
        const int iterations = 100;
        var totalExpected = data.Length * iterations;

        var readTask = Task.Run(async () =>
        {
            var received = new byte[totalExpected];
            var totalRead = 0;
            while (totalRead < totalExpected)
            {
                var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }
            return (received, totalRead);
        });

        for (int i = 0; i < iterations; i++)
        {
            await writeChannel.WriteAsync(data, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);

        var (received, receivedLen) = await readTask;
        Assert.Equal(totalExpected, receivedLen);
        for (int i = 0; i < iterations; i++)
        {
            Assert.Equal(data, received.AsSpan(i * data.Length, data.Length).ToArray());
        }
    }

    [Fact(Timeout = 60000)]
    public async Task WriteChannel_ZeroLengthWrite_Noop()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("credit_zero", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("credit_zero", cts.Token);

        await writeChannel.WriteAsync(ReadOnlyMemory<byte>.Empty, cts.Token);

        await writeChannel.WriteAsync(new byte[] { 0xCC }, cts.Token);
        var buf = new byte[1];
        var n = await readChannel.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xCC, buf[0]);
    }

    #endregion

    #region Credit Starvation Events

    [Theory(Timeout = 30000)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(50)]
    public async Task CreditStarvation_EventFires(int latencyMs)
    {
        await using var pipe = new DuplexPipe(latencyMs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions
            {
                MinCredits = 512,
                MaxCredits = 2048,
            },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("starvation", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("starvation", cts.Token);

        var starvationCount = 0;
        writeChannel.OnCreditStarvation += () => Interlocked.Increment(ref starvationCount);

        const int iterations = 20;
        var bigData = new byte[8192];
        Random.Shared.NextBytes(bigData);
        var totalExpected = iterations * bigData.Length;

        var drainTask = Task.Run(async () =>
        {
            var received = new byte[totalExpected];
            var totalRead = 0;
            while (totalRead < totalExpected)
            {
                var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
                await Task.Delay(10, cts.Token);
            }
            return (received, totalRead);
        });

        for (int i = 0; i < iterations; i++)
        {
            await writeChannel.WriteAsync(bigData, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);

        var (receivedData, receivedLen) = await drainTask;

        Assert.Equal(totalExpected, receivedLen);
        for (int i = 0; i < iterations; i++)
        {
            Assert.Equal(bigData, receivedData.AsSpan(i * bigData.Length, bigData.Length).ToArray());
        }
        Assert.True(starvationCount > 0, $"Expected credit starvation events, got {starvationCount}");

        await cts.CancelAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task CreditRestored_EventFiresWithDuration()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 512, MaxCredits = 2048 },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("starvation_restore", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("starvation_restore", cts.Token);

        var restoredDurations = new ConcurrentBag<TimeSpan>();
        writeChannel.OnCreditRestored += duration => restoredDurations.Add(duration);

        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        const int iterations = 10;
        var totalExpected = data.Length * iterations;

        var drainTask = Task.Run(async () =>
        {
            var received = new byte[totalExpected];
            var totalRead = 0;
            while (totalRead < totalExpected)
            {
                try
                {
                    var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (n == 0) break;
                    totalRead += n;
                    await Task.Delay(5, cts.Token);
                }
                catch { break; }
            }
            return (received, totalRead);
        });

        for (int i = 0; i < iterations; i++)
        {
            try { await writeChannel.WriteAsync(data, cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { break; }
        }
        await writeChannel.CloseAsync(cts.Token);

        var (received, receivedLen) = await drainTask;
        Assert.Equal(totalExpected, receivedLen);
        for (int i = 0; i < iterations; i++)
        {
            Assert.Equal(data, received.AsSpan(i * data.Length, data.Length).ToArray());
        }

        foreach (var d in restoredDurations)
            Assert.True(d >= TimeSpan.Zero, $"Duration was negative: {d}");
    }

    [Fact(Timeout = 30000)]
    public async Task CreditStarvation_EventHandlerThrows_WriteStillSucceeds()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 512, MaxCredits = 1024 },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("starvation_throw", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("starvation_throw", cts.Token);

        writeChannel.OnCreditStarvation += () => throw new InvalidOperationException("test exception");
        writeChannel.OnCreditRestored += _ => throw new InvalidOperationException("restore exception");

        var data = new byte[2048];
        Random.Shared.NextBytes(data);

        var readTask = Task.Run(async () =>
        {
            var received = new byte[data.Length];
            var totalRead = 0;
            while (totalRead < data.Length)
            {
                try 
                { 
                    var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (n == 0) break;
                    totalRead += n;
                }
                catch { break; }
            }
            return received[..totalRead];
        });

        await writeChannel.WriteAsync(data, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        var result = await readTask;
        Assert.Equal(data, result);
    }

    #endregion

    #region Multi-Channel Credit Starvation Under Latency

    [Theory(Timeout = 120000)]
    [InlineData(20, 0)]
    [InlineData(20, 5)]
    [InlineData(20, 50)]
    [InlineData(50, 0)]
    [InlineData(50, 5)]
    public async Task ManyChannels_SmallCredits_WithLatency_AllDataDelivered(int channelCount, int latencyMs)
    {
        await using var pipe = new DuplexPipe(latencyMs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions
            {
                MinCredits = 512,
                MaxCredits = 2048,
            },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var dataPerChannel = new byte[4096];
        Random.Shared.NextBytes(dataPerChannel);

        var totalStarvation = 0;

        var writers = new WriteChannel[channelCount];
        var readers = new ReadChannel[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = await muxA.OpenChannelAsync($"ch_{i}", cts.Token);
            readers[i] = await muxB.AcceptChannelAsync($"ch_{i}", cts.Token);
            writers[i].OnCreditStarvation += () => Interlocked.Increment(ref totalStarvation);
        }

        // Read all channels concurrently with slow drain
        var readTasks = new Task<byte[]>[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var reader = readers[i];
            readTasks[i] = Task.Run(async () =>
            {
                var received = new byte[dataPerChannel.Length];
                var totalRead = 0;
                while (totalRead < dataPerChannel.Length)
                {
                    var n = await reader.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (n == 0) break;
                    totalRead += n;
                }
                return received[..totalRead];
            });
        }

        // Write all channels concurrently
        var writeTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var writer = writers[i];
            writeTasks[i] = Task.Run(async () =>
            {
                await writer.WriteAsync(dataPerChannel, cts.Token);
                await writer.CloseAsync(cts.Token);
            });
        }

        await Task.WhenAll(writeTasks);
        var results = await Task.WhenAll(readTasks);

        // Verify data integrity on every channel
        for (int i = 0; i < channelCount; i++)
        {
            Assert.Equal(dataPerChannel.Length, results[i].Length);
            Assert.Equal(dataPerChannel, results[i]);
        }

        // With small credits and many channels, starvation must occur
        Assert.True(totalStarvation > 0,
            $"Expected credit starvation with {channelCount} channels and {opts.DefaultChannelOptions.MaxCredits} max credits, got 0 events");

        await cts.CancelAsync();
    }

    #endregion

    #region Adaptive Flow Control

    [Fact]
    public void FlowControl_ShrinkDuringGrant_WindowAdjusts()
    {
        var afc = new AdaptiveFlowControl(512, 4 * 1024 * 1024);

        var grant1 = afc.RecordConsumptionAndGetGrant(1024 * 1024);

        for (int i = 0; i < 10; i++)
        {
            afc.TryShrinkIfIdle();
        }

        var windowAfterShrink = afc.CurrentWindowSize;

        var grant2 = afc.RecordConsumptionAndGetGrant(2048);
    }

    [Fact]
    public void FlowControl_SmallConsumptions_NeverExceedMax()
    {
        var afc = new AdaptiveFlowControl(512, 4096);

        for (int i = 0; i < 1000; i++)
        {
            var grant = afc.RecordConsumptionAndGetGrant(1);
            if (grant > 0)
                Assert.True(grant <= 4096, $"Grant {grant} exceeded max 4096");
        }
    }

    [Fact]
    public void FlowControl_WindowNeverBelowMin()
    {
        var afc = new AdaptiveFlowControl(512, 4096);

        for (int i = 0; i < 100; i++)
            afc.TryShrinkIfIdle();

        Assert.True(afc.CurrentWindowSize >= 512, $"Window {afc.CurrentWindowSize} went below min 512");
    }

    [Fact]
    public void FlowControl_InitialWindowIsMax()
    {
        var afc = new AdaptiveFlowControl(512, 4096);
        Assert.Equal(4096u, afc.CurrentWindowSize);
    }

    [Fact]
    public void FlowControl_GetInitialCredits_EqualsWindowSize()
    {
        var afc = new AdaptiveFlowControl(1024, 8192);
        Assert.Equal(afc.CurrentWindowSize, afc.GetInitialCredits());
    }

    [Fact]
    public void FlowControl_LargeConsumption_GrantsCredits()
    {
        var afc = new AdaptiveFlowControl(512, 4096);

        var grant = afc.RecordConsumptionAndGetGrant(2048);
        Assert.True(grant > 0, "Expected a grant after consuming 2048 bytes");
    }

    #endregion
}
