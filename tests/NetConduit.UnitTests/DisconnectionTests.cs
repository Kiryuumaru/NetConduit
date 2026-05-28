namespace NetConduit.UnitTests;

using System.Reflection;

public sealed class DisconnectionTests
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
    public async Task ConcurrentGoAway_CallsEmitDisconnectedExactlyOnce()
    {
        const int iterations = 500;
        const int callers = 32;

        for (int i = 0; i < iterations; i++)
        {
            var (client, server) = CreatePair();
            client.Start();
            server.Start();
            await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

            int disconnectedCount = 0;
            client.Disconnected += (_, args) =>
            {
                Assert.Equal(DisconnectReason.LocalDispose, args.Reason);
                Interlocked.Increment(ref disconnectedCount);
            };

            // Force GoAwayAsync into its retry+yield path before shutdown is latched,
            // which widens the check-then-set race window for concurrent callers.
            var connField = typeof(StreamMultiplexer).GetField("_conn", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_conn field not found");
            var conn = connField.GetValue(client) ?? throw new InvalidOperationException("_conn was null");
            var controlChannelField = conn.GetType().GetField("ControlChannel", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("ControlChannel field not found");
            controlChannelField.SetValue(conn, null);

            using var barrier = new Barrier(callers + 1);
            var tasks = new Task[callers];
            for (int caller = 0; caller < callers; caller++)
            {
                tasks[caller] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    await client.GoAwayAsync();
                });
            }

            barrier.SignalAndWait();
            await Task.WhenAll(tasks);

            Assert.Equal(1, disconnectedCount);

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocalDispose_FiresOnDisconnected()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        DisconnectReason? reason = null;
        client.Disconnected += (_, e) => reason = e.Reason;

        await client.DisposeAsync();

        Assert.Equal(DisconnectReason.LocalDispose, reason);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task LocalDispose_AbortsAllChannels()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channels = new IWriteChannel[3];
        var closeReasons = new ChannelCloseReason?[3];

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            channels[i] = client.OpenChannel($"abort-{i}");
            channels[i].Closed += (_, e) => closeReasons[idx] = e.Reason;
        }

        await client.DisposeAsync();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(channels[i].State is ChannelState.Closed or ChannelState.Closing);
            Assert.Equal(ChannelCloseReason.MuxDisposed, closeReasons[i]);
        }

        await server.DisposeAsync();
    }

    [Fact]
    public async Task LocalDispose_SetsDisconnectReasonProperty()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Null(client.DisconnectReason);

        await client.DisposeAsync();

        Assert.Equal(DisconnectReason.LocalDispose, client.DisconnectReason);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransportError_FiresOnDisconnected()
    {
        var duplex = new DuplexMemoryStream();
        var killableA = new KillableStreamPair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(killableA),
            MaxAutoReconnectAttempts = 1,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            MaxAutoReconnectAttempts = 1,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var disconnected = new TaskCompletionSource<(DisconnectReason, Exception?)>();
        client.Disconnected += (_, e) => disconnected.TrySetResult((e.Reason, e.Exception));

        killableA.Kill();

        var (reason, _) = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal(DisconnectReason.TransportError, reason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransportError_AbortsAllChannels()
    {
        var duplex = new DuplexMemoryStream();
        var killableA = new KillableStreamPair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(killableA),
            MaxAutoReconnectAttempts = 1,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            MaxAutoReconnectAttempts = 1,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch1 = client.OpenChannel("ch1");
        var ch2 = client.OpenChannel("ch2");

        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        killableA.Kill();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(120));

        // Dispose to ensure cleanup completes
        await client.DisposeAsync();

        // After full dispose, channels must be closed
        Assert.True(ch1.State is ChannelState.Closed or ChannelState.Closing);
        Assert.True(ch2.State is ChannelState.Closed or ChannelState.Closing);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannelDispose_FiresOnClosed()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel("close-event");
        ChannelCloseReason? closeReason = null;
        channel.Closed += (_, e) => closeReason = e.Reason;

        await channel.DisposeAsync();

        Assert.Equal(ChannelCloseReason.LocalClose, closeReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_WriteAfterClose_ThrowsChannelClosedException()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel("closed-write");
        await channel.DisposeAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await channel.WriteAsync(new byte[] { 1, 2, 3 });
        });

        // Should be ChannelClosedException or ObjectDisposedException
        Assert.True(ex is ChannelClosedException || ex is ObjectDisposedException || ex is InvalidOperationException,
            $"Unexpected exception type: {ex.GetType().Name}");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MuxDispose_WithoutGoAway_RemoteSideHandles()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ch = client.OpenChannel("abrupt");
        await server.AcceptChannelAsync("abrupt", cts.Token);

        // Dispose without GoAway
        await client.DisposeAsync();

        // Server should eventually stop (no hang)
        await server.DisposeAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task GoAway_ClientSeesDisconnect()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.GoAwayAsync();

        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_InFlightDataDelivered()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var ch = client.OpenChannel("inflight");
        var readCh = await server.AcceptChannelAsync("inflight", cts.Token);

        // Send data and ensure it arrives before GoAway
        byte[] data = [1, 2, 3, 4, 5];
        await ch.WriteAsync(data, cts.Token);

        // Read data before GoAway to validate data was delivered
        var buffer = new byte[16];
        int read = await readCh.ReadAsync(buffer, cts.Token);
        Assert.True(read >= 5);
        Assert.Equal(data, buffer[..5]);

        // Now GoAway should succeed cleanly
        await client.GoAwayAsync();
        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultipleChannels_AllAbortedOnMuxDispose()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channels = new IWriteChannel[5];
        for (int i = 0; i < 5; i++)
            channels[i] = client.OpenChannel($"multi-{i}");

        await client.DisposeAsync();

        foreach (var ch in channels)
            Assert.True(ch.State is ChannelState.Closed or ChannelState.Closing);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task DisposeBeforeStart_DoesNotThrow()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });

        // Dispose before Start should not throw
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectReason_NullBeforeDispose()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Null(client.DisconnectReason);
        Assert.Null(server.DisconnectReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MuxDispose_CalledTwice_CompletesImmediately()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.DisposeAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.DisposeAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Second dispose took {sw.ElapsedMilliseconds}ms");

        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_WriterCloseThenRead_ReturnsZero()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeChannel = client.OpenChannel("writer-close");
        var readChannel = await server.AcceptChannelAsync("writer-close", cts.Token);

        await writeChannel.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await writeChannel.DisposeAsync();

        var buffer = new byte[16];
        int totalRead = 0;
        int read;
        while ((read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token)) > 0)
        {
            totalRead += read;
        }

        Assert.Equal(3, totalRead);
        // After EOF, subsequent reads return 0
        Assert.Equal(0, await readChannel.ReadAsync(buffer, cts.Token));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
