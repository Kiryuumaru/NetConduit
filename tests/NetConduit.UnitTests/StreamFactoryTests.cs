namespace NetConduit.UnitTests;

public sealed class StreamFactoryTests
{
    [Fact]
    public async Task StreamFactory_CalledOnStart()
    {
        int callCount = 0;
        var duplex = new DuplexMemoryStream();

        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult<IStreamPair>(duplex.SideA);
            },
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });

        mux.Start();
        server.Start();
        await Task.WhenAll(mux.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.True(callCount >= 1);

        await mux.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamFactory_ReceivesCancellationToken()
    {
        CancellationToken? receivedToken = null;
        var duplex = new DuplexMemoryStream();

        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct =>
            {
                receivedToken = ct;
                return Task.FromResult<IStreamPair>(duplex.SideA);
            },
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });

        mux.Start();
        server.Start();
        await Task.WhenAll(mux.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.NotNull(receivedToken);
        Assert.False(receivedToken!.Value.IsCancellationRequested);

        await mux.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamFactory_Failure_MuxHandlesGracefully()
    {
        int attempts = 0;
        var duplex = new DuplexMemoryStream();

        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("Connection failed");
                return Task.FromResult<IStreamPair>(duplex.SideA);
            },
        });

        Exception? errorRaised = null;
        mux.Error += (_, e) => errorRaised = e.Exception;

        mux.Start();

        // Give it time to fail and potentially retry
        await Task.Delay(500);

        // The mux should have attempted at least once and handled the error
        Assert.True(attempts >= 1);

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task OnReconnecting_EventFires()
    {
        var broker = new TransportBroker(3);
        int reconnectingCount = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Reconnecting += (_, _) => Interlocked.Increment(ref reconnectingCount);

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Kill round 0 to trigger reconnection
        broker.KillRound(0);

        // Wait for reconnect attempt
        await Task.Delay(3000);

        Assert.True(reconnectingCount >= 1, $"OnReconnecting fired {reconnectingCount} times");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Reconnection_DataFlowResumes()
    {
        var broker = new TransportBroker(3);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writeCh = client.OpenChannel("resume");
        var readCh = await server.AcceptChannelAsync("resume", cts.Token);

        // Send initial data
        await writeCh.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        var buf = new byte[16];
        int read = await readCh.ReadAsync(buf, cts.Token);
        Assert.Equal(3, read);

        // Kill round 0 and wait for reconnection on round 1
        var reconnected = new TaskCompletionSource();
        client.Connected += (_, _) => reconnected.TrySetResult();
        broker.KillRound(0);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Send more data after reconnection
        await writeCh.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);
        read = await readCh.ReadAsync(buf, cts.Token);
        Assert.True(read > 0);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Reconnection_ChannelsSurvive()
    {
        var broker = new TransportBroker(3);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Open channels before disconnect
        var ch1 = client.OpenChannel("survive-1");
        var ch2 = client.OpenChannel("survive-2");
        await server.AcceptChannelAsync("survive-1", cts.Token);
        await server.AcceptChannelAsync("survive-2", cts.Token);
        await ch1.WaitForReadyAsync(cts.Token);
        await ch2.WaitForReadyAsync(cts.Token);

        // Kill round 0
        var reconnected = new TaskCompletionSource();
        client.Connected += (_, _) => reconnected.TrySetResult();
        broker.KillRound(0);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Channels should still be ready after reconnect
        Assert.True(ch1.IsReady);
        Assert.True(ch2.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Reconnection_MultipleDisconnects_AllRecover()
    {
        var broker = new TransportBroker(5);
        int connectCount = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Connected += (_, _) => Interlocked.Increment(ref connectCount);

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Kill and reconnect 3 times (rounds 0, 1, 2)
        for (int i = 0; i < 3; i++)
        {
            broker.KillRound(i);
            await Task.Delay(3000);
        }

        // Should have reconnected multiple times
        Assert.True(connectCount >= 2, $"Connected {connectCount} times");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MaxAutoReconnectAttempts_Exhausted_StopsReconnecting()
    {
        // Only provide 1 round - after kill, reconnection will fail
        var duplex = new DuplexMemoryStream();
        var killable = new KillableStreamPair(duplex.SideA);
        int factoryCalls = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                int call = Interlocked.Increment(ref factoryCalls);
                if (call == 1)
                    return Task.FromResult<IStreamPair>(killable);
                // Subsequent calls fail
                throw new InvalidOperationException("No more transports");
            },
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

        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        killable.Kill();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // After max attempts exhausted, should stay disconnected
        Assert.False(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
