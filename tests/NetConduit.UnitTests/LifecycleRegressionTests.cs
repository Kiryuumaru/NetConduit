namespace NetConduit.UnitTests;

public sealed class LifecycleRegressionTests
{
    [Fact]
    public async Task Heartbeat_WithIntervalLongerThanTimeout_KeepsHealthyPeersConnected()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.FromMilliseconds(200),
            PingTimeout = TimeSpan.FromMilliseconds(50),
            MaxMissedPings = 2,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.FromMilliseconds(200),
            PingTimeout = TimeSpan.FromMilliseconds(50),
            MaxMissedPings = 2,
            MaxAutoReconnectAttempts = 0,
        });

        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        var observation = Task.Delay(TimeSpan.FromMilliseconds(750), cts.Token);
        var completed = await Task.WhenAny(disconnected.Task, observation);

        Assert.Equal(observation, completed);
        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransportFailure_WithReconnectDisabled_DoesNotAttemptReconnect()
    {
        await using var factory = new ReconnectableTransportFactory();
        int reconnectAttempts = 0;
        int connectedCount = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = factory.CreateSideA,
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        client.Connected += (_, _) => Interlocked.Increment(ref connectedCount);
        client.Reconnecting += (_, _) => Interlocked.Increment(ref reconnectAttempts);

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = factory.CreateSideB,
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        Assert.Equal(1, factory.ConnectionCount);

        factory.KillCurrentTransport();

        var args = await disconnected.Task.WaitAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cts.Token);

        Assert.Equal(DisconnectReason.TransportError, args.Reason);
        Assert.Equal(0, reconnectAttempts);
        Assert.Equal(1, connectedCount);
        Assert.Equal(1, factory.ConnectionCount);
        Assert.False(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectExhaustion_ClosesOpenChannelsWithTransportFailed()
    {
        var duplex = new DuplexMemoryStream();
        var killableClient = new KillableStreamPair(duplex.SideA);
        int clientFactoryCalls = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                int call = Interlocked.Increment(ref clientFactoryCalls);
                if (call == 1)
                    return Task.FromResult<IStreamPair>(killableClient);

                throw new IOException("Replacement transport unavailable.");
            },
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("bounded-reconnect");
        await server.AcceptChannelAsync("bounded-reconnect", cts.Token);
        await writer.WaitForReadyAsync(cts.Token);

        var closed = new TaskCompletionSource<ChannelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Closed += (_, e) => closed.TrySetResult(e.Reason);

        killableClient.Kill();

        var reason = await closed.Task.WaitAsync(cts.Token);

        Assert.Equal(ChannelCloseReason.TransportFailed, reason);
        Assert.Equal(ChannelState.Closed, writer.State);
        Assert.Equal(ChannelCloseReason.TransportFailed, writer.CloseReason);
        Assert.Equal(2, clientFactoryCalls);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
