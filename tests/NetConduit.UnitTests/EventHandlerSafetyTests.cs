namespace NetConduit.UnitTests;

public sealed class EventHandlerSafetyTests
{
    private static StreamMultiplexer CreateClient(DuplexMemoryStream duplex) =>
        StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

    private static StreamMultiplexer CreateServer(DuplexMemoryStream duplex) =>
        StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

    [Fact]
    public async Task ThrowingReadyHandler_DoesNotStopRemainingHandlers()
    {
        var duplex = new DuplexMemoryStream();
        var client = CreateClient(duplex);
        var server = CreateServer(duplex);

        var secondHandlerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Ready += (_, _) => throw new InvalidOperationException("first handler boom");
        client.Ready += (_, _) => secondHandlerRan.TrySetResult();

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        await secondHandlerRan.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ThrowingHandler_RoutesExceptionToErrorEvent()
    {
        var duplex = new DuplexMemoryStream();
        var client = CreateClient(duplex);
        var server = CreateServer(duplex);

        var thrown = new InvalidOperationException("handler boom");
        var captured = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.Error += (_, e) => captured.TrySetResult(e.Exception);
        client.Ready += (_, _) => throw thrown;

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        var seen = await captured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(thrown, seen);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ThrowingErrorHandler_DoesNotRecurseOrCrashMux()
    {
        var duplex = new DuplexMemoryStream();
        var client = CreateClient(duplex);
        var server = CreateServer(duplex);

        int errorInvocations = 0;
        client.Error += (_, _) =>
        {
            Interlocked.Increment(ref errorInvocations);
            throw new InvalidOperationException("error handler boom");
        };
        client.Ready += (_, _) => throw new InvalidOperationException("ready handler boom");

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Give any (illegal) recursion a chance to manifest.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, Volatile.Read(ref errorInvocations));
        Assert.True(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ThrowingDisconnectedHandler_DoesNotStopRemainingHandlers()
    {
        var duplex = new DuplexMemoryStream();
        var client = CreateClient(duplex);
        var server = CreateServer(duplex);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => throw new InvalidOperationException("first disconnected boom");
        client.Disconnected += (_, _) => secondRan.TrySetResult();

        await client.DisposeAsync();

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.DisposeAsync();
    }
}
