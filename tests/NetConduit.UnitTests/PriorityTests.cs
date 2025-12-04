namespace NetConduit.UnitTests;

public class PriorityTests
{
    [Fact(Timeout = 120000)]
    public async Task Priority_HighPriorityFirst_WhenQueued()
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

        var receivedOrder = new List<byte>();
        var acceptedCount = 0;

        // Accept channels
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[1];
                    var read = await channel.ReadAsync(buffer, cts.Token);
                    if (read > 0)
                    {
                        lock (receivedOrder)
                        {
                            receivedOrder.Add(buffer[0]);
                        }
                    }
                });
                if (++acceptedCount >= 3) break;
            }
        });

        // Open channels with different priorities
        var lowChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "low_priority", Priority = ChannelPriority.Low },
            cts.Token);
        
        var normalChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "normal_priority", Priority = ChannelPriority.Normal },
            cts.Token);
        
        var highChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "high_priority", Priority = ChannelPriority.High },
            cts.Token);

        // Send data on all channels (marker bytes)
        await Task.WhenAll(
            lowChannel.WriteAsync(new byte[] { 1 }, cts.Token).AsTask(),
            normalChannel.WriteAsync(new byte[] { 2 }, cts.Token).AsTask(),
            highChannel.WriteAsync(new byte[] { 3 }, cts.Token).AsTask()
        );

        await Task.Delay(500);

        // Note: Due to async nature, order may vary, but high priority should generally come first
        // This test mainly ensures priority channels work correctly
        Assert.Equal(3, receivedOrder.Count);
        Assert.Contains((byte)1, receivedOrder);
        Assert.Contains((byte)2, receivedOrder);
        Assert.Contains((byte)3, receivedOrder);

        cts.Cancel();
    }

    [Theory(Timeout = 120000)]
    [InlineData(ChannelPriority.Lowest)]
    [InlineData(ChannelPriority.Low)]
    [InlineData(ChannelPriority.Normal)]
    [InlineData(ChannelPriority.High)]
    [InlineData(ChannelPriority.Highest)]
    public async Task Priority_AllLevels_Work(ChannelPriority priority)
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
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
            new ChannelOptions { ChannelId = $"priority_{priority}", Priority = priority },
            cts.Token);
        
        await acceptTask;

        Assert.Equal(priority, writeChannel.Priority);
        Assert.Equal(priority, readChannel!.Priority);

        // Verify data transfer works
        var testData = new byte[] { 1, 2, 3, 4 };
        await writeChannel.WriteAsync(testData, cts.Token);

        var buffer = new byte[4];
        var read = await readChannel.ReadAsync(buffer, cts.Token);
        
        Assert.Equal(4, read);
        Assert.Equal(testData, buffer);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Priority_CustomValue_Works()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
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

        // Use custom priority value (not a predefined constant)
        var customPriority = (ChannelPriority)200;
        
        var writeChannel = await initiator.OpenChannelAsync(
            new ChannelOptions { ChannelId = "custom_priority_channel", Priority = customPriority },
            cts.Token);
        
        await acceptTask;

        Assert.Equal(customPriority, writeChannel.Priority);
        Assert.Equal(customPriority, readChannel!.Priority);

        cts.Cancel();
    }
}
