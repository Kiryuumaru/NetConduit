using NetConduit.Constants;

namespace NetConduit.UnitTests;

public sealed class GoAwayTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        return (client, server);
    }

    [Fact]
    public async Task GoAway_SetsShuttingDown()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.False(client.IsShuttingDown);

        await client.GoAwayAsync();

        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RemoteSideReceivesGoAway()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var disconnectTcs = new TaskCompletionSource<DisconnectReason>();
        server.Disconnected += (_, e) => disconnectTcs.TrySetResult(e.Reason);

        await client.GoAwayAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Server must detect client shutdown (via GoAway frame or transport close)
        var reason = await disconnectTcs.Task.WaitAsync(cts.Token);
        Assert.True(reason is DisconnectReason.GoAwayReceived or DisconnectReason.TransportError);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_Idempotent()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.GoAwayAsync();
        await client.GoAwayAsync(); // second call should be no-op

        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_WaitsForOpenChannelsThenAbortsOnTimeout()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(500),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writer = client.OpenChannel("draining");
        await server.AcceptChannelAsync("draining", CancellationToken.None);
        await writer.WaitForReadyAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.GoAwayAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"GoAwayAsync returned in {stopwatch.ElapsedMilliseconds} ms, but should have waited the drain timeout.");
        Assert.Equal(ChannelState.Closed, writer.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, writer.CloseReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_ReturnsImmediatelyWhenChannelsAlreadyClosed()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(30),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.GoAwayAsync();
        stopwatch.Stop();

        // No channels open => drain returns immediately without waiting the 30 s timeout.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"GoAwayAsync with no open channels took {stopwatch.ElapsedMilliseconds} ms; should be near-zero.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RejectsNewOpenChannel()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.GoAwayAsync();

        Assert.Throws<InvalidOperationException>(() => client.OpenChannel("after-goaway"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // Regression for: remote-initiated GoAway must terminally abort
    // local channels so awaiting reads see EOF and writers see ChannelClosedException.

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePairWithShortDrain()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
        });
        return (client, server);
    }

    [Fact]
    public async Task GoAway_RemoteInitiated_ClosesLocalChannels()
    {
        var (client, server) = CreatePairWithShortDrain();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var clientChannel = client.OpenChannel("payload");
        var serverChannel = await server.AcceptChannelAsync("payload", CancellationToken.None);
        await clientChannel.WaitForReadyAsync();
        await serverChannel.WaitForReadyAsync();

        Assert.Equal(ChannelState.Open, serverChannel.State);

        // Client initiates GoAway; server receives it via the wire.
        await client.GoAwayAsync();

        // Wait for server-side closure to propagate.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (serverChannel.State != ChannelState.Closed && !cts.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        Assert.Equal(ChannelState.Closed, serverChannel.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, serverChannel.CloseReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RemoteInitiated_PendingReadAsyncReturnsEofPromptly()
    {
        var (client, server) = CreatePairWithShortDrain();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var clientChannel = client.OpenChannel("payload");
        var serverChannel = await server.AcceptChannelAsync("payload", CancellationToken.None);
        await clientChannel.WaitForReadyAsync();
        await serverChannel.WaitForReadyAsync();

        // Park a server-side ReadAsync with no buffered data — slow path.
        var sink = new byte[64];
        var readTask = serverChannel.ReadAsync(sink, CancellationToken.None).AsTask();

        // Give the read a moment to actually park.
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        // Client GoAways. Server must wake the parked read with EOF (0 bytes).
        // Use a short GoAwayTimeout so client's own drain doesn't dominate timing.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.GoAwayAsync();

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5)));
        sw.Stop();

        Assert.True(ReferenceEquals(completed, readTask),
            $"ReadAsync did not unwind after remote GoAway (waited {sw.ElapsedMilliseconds} ms).");

        int n = await readTask;
        Assert.Equal(0, n);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"ReadAsync took {sw.ElapsedMilliseconds} ms to observe remote GoAway; expected prompt EOF.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RemoteInitiated_ClosesLocalWriteChannel_AndWriteAsyncThrowsChannelClosed()
    {
        var (client, server) = CreatePairWithShortDrain();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var clientChannel = client.OpenChannel("payload");
        var serverChannel = await server.AcceptChannelAsync("payload", CancellationToken.None);
        await clientChannel.WaitForReadyAsync();
        await serverChannel.WaitForReadyAsync();

        Assert.Equal(ChannelState.Open, clientChannel.State);

        // Server initiates GoAway; client receives it via the wire and must abort
        // its local channels rather than leaving them in Open state.
        await server.GoAwayAsync();

        // Wait for client-side closure to propagate.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (clientChannel.State != ChannelState.Closed && !cts.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        Assert.Equal(ChannelState.Closed, clientChannel.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, clientChannel.CloseReason);

        // Subsequent WriteAsync must throw ChannelClosedException (state-check at
        // top of WriteAsync), not TimeoutException after waiting SendTimeout.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(
            async () => await clientChannel.WriteAsync(new byte[64], CancellationToken.None));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"WriteAsync on closed channel took {sw.ElapsedMilliseconds} ms; expected immediate throw.");
        Assert.Equal(ChannelCloseReason.MuxDisposed, ex.CloseReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
