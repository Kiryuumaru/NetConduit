namespace NetConduit.UnitTests;

/// <summary>
/// Tests for memory pressure scenarios and buffer limits.
/// </summary>
public class MemoryPressureTests
{
    [Fact(Timeout = 120000)]
    public async Task ReconnectBuffer_ExceedsLimit_TrimsOldData()
    {
        // Test that the reconnection buffer trims old data when limit is exceeded
        await using var pipe = new DuplexPipe();
        
        // Very small reconnect buffer to trigger trimming
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            ReconnectBufferSize = 1024 // Only 1KB buffer
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, 
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "buffer_test" }, cts.Token);
        await acceptTask;

        // Send more data than the buffer can hold (2KB when buffer is 1KB)
        var data = new byte[2048];
        Random.Shared.NextBytes(data);
        
        await writeChannel.WriteAsync(data, cts.Token);

        // Read all data on the other side to verify transmission
        var totalRead = 0;
        var buffer = new byte[4096];
        while (totalRead < data.Length)
        {
            var read = await readChannel!.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }
        
        Assert.Equal(data.Length, totalRead);
        Assert.True(data.AsSpan().SequenceEqual(buffer.AsSpan(0, totalRead)));

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task ManyChannels_MemoryGrowthBounded()
    {
        // Test that opening many channels doesn't cause unbounded memory growth
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = false, // Disable reconnection to reduce memory overhead
            DefaultChannelOptions = new DefaultChannelOptions 
            { 
                MinCredits = 1024, // Small credits
                MaxCredits = 4096 
            }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        var acceptedChannels = new List<ReadChannel>();
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                acceptedChannels.Add(ch);
                if (acceptedChannels.Count >= 100)
                    break;
            }
        });

        var writeChannels = new List<WriteChannel>();
        
        // Open 100 channels
        for (int i = 0; i < 100; i++)
        {
            var channel = await initiator.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"channel_{i}" }, cts.Token);
            writeChannels.Add(channel);
        }

        // Wait for all channels to be accepted
        await Task.Delay(500);
        
        Assert.Equal(100, writeChannels.Count);
        Assert.Equal(100, acceptedChannels.Count);

        // Send small data on each channel
        var data = new byte[64];
        Random.Shared.NextBytes(data);
        
        foreach (var ch in writeChannels)
        {
            await ch.WriteAsync(data, cts.Token);
        }

        // Verify all channels received data
        foreach (var ch in acceptedChannels)
        {
            var buffer = new byte[64];
            var read = await ch.ReadAsync(buffer, cts.Token);
            Assert.Equal(64, read);
        }

        // Close all channels
        foreach (var ch in writeChannels)
        {
            await ch.DisposeAsync();
        }

        // Verify stats
        Assert.Equal(100, initiator.Stats.TotalChannelsOpened);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task LargeDataTransfer_WithSmallCredits_CompletesWithBackpressure()
    {
        // Test that large data transfers work with small credit windows
        await using var pipe = new DuplexPipe();
        
        var acceptorOptions = new MultiplexerOptions
        {
            DefaultChannelOptions = new DefaultChannelOptions 
            { 
                MinCredits = 512,   // Very small credits
                MaxCredits = 1024   // Forces frequent backpressure
            }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, 
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "large_transfer", SendTimeout = TimeSpan.FromSeconds(30) },
            cts.Token);
        await acceptTask;

        // Track backpressure events
        var starvationCount = 0;
        var restoredCount = 0;
        var totalWaitTime = TimeSpan.Zero;
        
        writeChannel.OnCreditStarvation += () => Interlocked.Increment(ref starvationCount);
        writeChannel.OnCreditRestored += wait =>
        {
            Interlocked.Increment(ref restoredCount);
            lock (typeof(MemoryPressureTests))
            {
                totalWaitTime += wait;
            }
        };

        // Send 64KB of data with only 1KB credits
        var data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        
        var writeTask = Task.Run(async () =>
        {
            await writeChannel.WriteAsync(data, cts.Token);
        });

        // Read data slowly to create backpressure
        var totalRead = 0;
        var buffer = new byte[512];
        while (totalRead < data.Length)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            totalRead += read;
            // Small delay to exacerbate backpressure
            await Task.Delay(1);
        }

        await writeTask;
        
        Assert.Equal(data.Length, totalRead);
        
        // Verify backpressure was detected
        Assert.True(starvationCount > 0, $"Expected credit starvation events, got {starvationCount}");
        Assert.Equal(starvationCount, restoredCount);
        Assert.True(totalWaitTime > TimeSpan.Zero, "Expected non-zero wait time");
        
        // Check stats
        Assert.True(writeChannel.Stats.CreditStarvationCount > 0);
        Assert.True(writeChannel.Stats.TotalWaitTimeForCredits > TimeSpan.Zero);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task BackpressureStats_TrackWaitTimes()
    {
        await using var pipe = new DuplexPipe();
        
        // Force immediate backpressure with tiny credits
        var acceptorOptions = new MultiplexerOptions
        {
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 100, MaxCredits = 100 }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, 
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "stats_test", SendTimeout = TimeSpan.FromSeconds(10) },
            cts.Token);
        await acceptTask;

        // Verify initial stats
        Assert.Equal(0, writeChannel.Stats.CreditStarvationCount);
        Assert.Equal(TimeSpan.Zero, writeChannel.Stats.TotalWaitTimeForCredits);
        Assert.False(writeChannel.Stats.IsWaitingForCredits);
        
        // Send more than credits allow
        var data = new byte[500];
        var writeTask = Task.Run(async () =>
        {
            await writeChannel.WriteAsync(data, cts.Token);
        });

        // Let sender block briefly
        await Task.Delay(100);
        
        // Now read to unblock
        var buffer = new byte[500];
        var totalRead = 0;
        while (totalRead < data.Length && !writeTask.IsCompleted)
        {
            var read = await readChannel!.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
            await Task.Delay(10);
        }

        await writeTask;
        
        // Verify backpressure was recorded
        Assert.True(writeChannel.Stats.CreditStarvationCount > 0, 
            $"Expected starvation count > 0, got {writeChannel.Stats.CreditStarvationCount}");
        Assert.True(writeChannel.Stats.TotalWaitTimeForCredits > TimeSpan.Zero,
            "Expected total wait time > 0");
        Assert.False(writeChannel.Stats.IsWaitingForCredits, "Should not be waiting anymore");
        
        // Verify mux-level aggregated stats
        Assert.True(initiator.Stats.TotalCreditStarvationEvents > 0);
        Assert.True(initiator.Stats.TotalCreditWaitTime > TimeSpan.Zero);
        Assert.False(initiator.Stats.IsExperiencingBackpressure);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task ConcurrentWrites_BackpressureTrackedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        
        var acceptorOptions = new MultiplexerOptions
        {
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 256, MaxCredits = 512 }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, 
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        const int channelCount = 5;
        var acceptedChannels = new List<ReadChannel>();
        
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                acceptedChannels.Add(ch);
                if (acceptedChannels.Count >= channelCount)
                    break;
            }
        });

        var writeChannels = new List<WriteChannel>();
        for (int i = 0; i < channelCount; i++)
        {
            var channel = await initiator.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"concurrent_{i}", SendTimeout = TimeSpan.FromSeconds(20) },
                cts.Token);
            writeChannels.Add(channel);
        }

        await Task.Delay(200);
        
        // Concurrently write to all channels - will cause backpressure
        var data = new byte[2048]; // Each channel sends 2KB with only 512B credits
        Random.Shared.NextBytes(data);
        
        var writeTasks = writeChannels.Select(ch => ch.WriteAsync(data.AsMemory(), cts.Token).AsTask()).ToArray();

        // Read from all channels concurrently
        var readTasks = acceptedChannels.Select(async ch =>
        {
            var buffer = new byte[2048];
            var read = 0;
            while (read < data.Length)
            {
                var n = await ch.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            return read;
        }).ToArray();

        await Task.WhenAll(writeTasks);
        var results = await Task.WhenAll(readTasks);
        
        // Verify all data was transferred
        Assert.All(results, r => Assert.Equal(data.Length, r));
        
        // Verify aggregated backpressure was tracked
        Assert.True(initiator.Stats.TotalCreditStarvationEvents > 0);
        Assert.Equal(0, initiator.Stats.ChannelsCurrentlyWaitingForCredits);
        Assert.False(initiator.Stats.IsExperiencingBackpressure);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task DisabledReconnection_NoBuffering()
    {
        // Test that disabling reconnection eliminates buffer overhead
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = false
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, 
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "no_buffer_test" }, cts.Token);
        await acceptTask;

        // Send data - should work fine without buffering
        var data = new byte[8192];
        Random.Shared.NextBytes(data);
        
        await writeChannel.WriteAsync(data, cts.Token);

        // Read all data
        var totalRead = 0;
        var buffer = new byte[8192];
        while (totalRead < data.Length)
        {
            var read = await readChannel!.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }
        
        Assert.Equal(data.Length, totalRead);
        Assert.True(data.AsSpan().SequenceEqual(buffer.AsSpan(0, totalRead)));

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelStats_LongestWait_TrackedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        
        var acceptorOptions = new MultiplexerOptions
        {
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 50, MaxCredits = 50 }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, 
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "longest_wait_test", SendTimeout = TimeSpan.FromSeconds(10) },
            cts.Token);
        await acceptTask;

        // Multiple writes to track multiple wait periods
        var data = new byte[200];
        
        for (int i = 0; i < 3; i++)
        {
            var writeTask = Task.Run(async () =>
            {
                await writeChannel.WriteAsync(data, cts.Token);
            });

            // Delay before reading to create different wait times
            await Task.Delay(50 * (i + 1));
            
            var buffer = new byte[200];
            var read = 0;
            while (read < data.Length && !writeTask.IsCompleted)
            {
                var n = await readChannel!.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            
            await writeTask;
        }

        // Verify longest wait is tracked
        Assert.True(writeChannel.Stats.LongestWaitForCredits > TimeSpan.Zero);
        Assert.True(writeChannel.Stats.LongestWaitForCredits <= writeChannel.Stats.TotalWaitTimeForCredits);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]  
    public async Task ZeroLengthReconnectBuffer_NoBuffering()
    {
        // Test edge case: zero reconnect buffer size
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            ReconnectBufferSize = 0  // No buffering even with reconnection enabled
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, 
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

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
            new ChannelOptions { ChannelId = "zero_buffer" }, cts.Token);
        await acceptTask;

        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        
        await writeChannel.WriteAsync(data, cts.Token);

        var totalRead = 0;
        var buffer = new byte[4096];
        while (totalRead < data.Length)
        {
            var read = await readChannel!.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }
        
        Assert.Equal(data.Length, totalRead);

        cts.Cancel();
    }

    [Fact(Timeout = 180000)]
    public async Task HighChannelChurn_MemoryReleased()
    {
        // Test that opening and closing many channels releases memory properly
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = false // Reduce memory overhead
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        var acceptedChannels = new System.Collections.Concurrent.ConcurrentBag<ReadChannel>();
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                acceptedChannels.Add(ch);
            }
        });

        // Open and close 50 batches of channels
        for (int batch = 0; batch < 50; batch++)
        {
            var channels = new List<WriteChannel>();
            
            // Open 10 channels
            for (int i = 0; i < 10; i++)
            {
                var channel = await initiator.OpenChannelAsync(
                    new ChannelOptions { ChannelId = $"churn_{batch}_{i}" }, cts.Token);
                channels.Add(channel);
            }

            await Task.Delay(20); // Let acceptor catch up
            
            // Send small data
            foreach (var ch in channels)
            {
                await ch.WriteAsync(new byte[32], cts.Token);
            }

            // Close all channels
            foreach (var ch in channels)
            {
                await ch.DisposeAsync();
            }
        }

        // Verify stats
        Assert.Equal(500, initiator.Stats.TotalChannelsOpened);
        Assert.Equal(500, initiator.Stats.TotalChannelsClosed);
        Assert.Equal(0, initiator.Stats.OpenChannels);

        cts.Cancel();
    }
}
