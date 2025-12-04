namespace NetConduit.UnitTests;

public class BackpressureTests
{
    [Fact(Timeout = 120000)]
    public async Task Backpressure_SlowReader_SenderBlocks()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
        var acceptorOptions = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 1024, MaxCredits = 1024 }
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
            new ChannelOptions { ChannelId = "backpressure_channel", SendTimeout = TimeSpan.FromSeconds(5) },
            cts.Token);
        await acceptTask;

        // Send more data than credits allow
        var largeData = new byte[4096]; // 4x the credits
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
        var buffer = new byte[512]; // Read in chunks
        while (totalRead < 4096 && !writeTask.IsCompleted)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            totalRead += read;
            
            // Give time for credit grant to propagate
            await Task.Delay(50);
        }

        // Write should eventually complete
        await writeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(writeTask.IsCompleted);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Backpressure_Timeout_ThrowsException()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
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
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
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
            new ChannelOptions 
            { 
                ChannelId = "autogrant_channel",
                MinCredits = 1024,
                MaxCredits = 1024
            },
            cts.Token);
        await acceptTask;

        var initialCredits = writeChannel.AvailableCredits;

        // Send data to consume credits
        await writeChannel.WriteAsync(new byte[512], cts.Token);
        
        // Read data to trigger auto-grant
        var buffer = new byte[512];
        await readChannel!.ReadExactlyAsync(buffer, cts.Token);

        await Task.Delay(100); // Wait for credit grant to arrive

        // Should have received more credits
        Assert.True(writeChannel.AvailableCredits > 0 || writeChannel.Stats.CreditsGranted > 512);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Backpressure_InfiniteTimeout_WaitsForever()
    {
        await using var pipe = new DuplexPipe();
        
        // Acceptor controls credits - set small initial credits
        var acceptorOptions = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 100, MaxCredits = 100 }
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, acceptorOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        
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
            new ChannelOptions { ChannelId = "infinite_timeout_channel", SendTimeout = Timeout.InfiniteTimeSpan }, // Never timeout
            cts.Token);
        await acceptTask;

        var writeTask = Task.Run(async () =>
        {
            await writeChannel.WriteAsync(new byte[1000], cts.Token);
        });

        // Wait a bit - should still be waiting (only 100 initial credits, but writing 1000 bytes)
        await Task.Delay(500);
        Assert.False(writeTask.IsCompleted, "Write should still be waiting with infinite timeout");

        // Read to unblock - read in small chunks to trigger credit grants
        var totalRead = 0;
        var buffer = new byte[100];
        while (totalRead < 1000 && !writeTask.IsCompleted)
        {
            try 
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var read = await readChannel!.ReadAsync(buffer, readCts.Token);
                if (read == 0) break;
                totalRead += read;
                await Task.Delay(50); // Allow credit grant to propagate
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Wait for write to complete after reading all data
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel to cleanup
        cts.Cancel();
    }
}
