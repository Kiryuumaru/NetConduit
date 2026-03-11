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
}



