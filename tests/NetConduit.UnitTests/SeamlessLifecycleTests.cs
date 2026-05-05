namespace NetConduit.UnitTests;

[Collection("Sequential")]
public sealed class SeamlessLifecycleTests
{
    // =====================================================================
    // Start() behavior — instant return, no connection required
    // =====================================================================

    [Fact]
    public void Start_ReturnsImmediately_WithoutConnection()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => throw new IOException("No network"),
            MaxAutoReconnectAttempts = 0,
        });

        mux.Start();

        Assert.True(mux.IsRunning);
        Assert.False(mux.IsConnected);
    }

    [Fact]
    public void Start_ThrowsIfCalledTwice()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });

        mux.Start();
        Assert.Throws<InvalidOperationException>(() => mux.Start());
    }

    // =====================================================================
    // OpenChannel before connection — seamless buffering
    // =====================================================================

    [Fact]
    public async Task OpenChannel_BeforeConnection_BuffersInSlab()
    {
        var connectGate = new TaskCompletionSource<DuplexMemoryStream>();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var duplex = await connectGate.Task.WaitAsync(ct);
                return duplex.SideA;
            },
            PingInterval = TimeSpan.Zero,
        });

        client.Start();

        // Open channel and write data BEFORE connection is established
        var ch = client.OpenChannel("pre-connect");
        await ch.WriteAsync(new byte[] { 0xCA, 0xFE });

        Assert.False(client.IsConnected);
        Assert.True(client.IsRunning);

        // Now let the connection establish
        var duplex = new DuplexMemoryStream();
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        server.Start();
        connectGate.SetResult(duplex);

        await client.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await server.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // The server should receive the buffered data
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rch = await server.AcceptChannelAsync("pre-connect", cts.Token);
        byte[] buf = new byte[2];
        int read = await rch.ReadAsync(buf, cts.Token);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, buf);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // OnConnected event
    // =====================================================================

    [Fact]
    public async Task OnConnected_FiresOnFirstConnect()
    {
        var duplex = new DuplexMemoryStream();
        int connectedCount = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        client.OnConnected += () => Interlocked.Increment(ref connectedCount);

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token),
            server.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        Assert.True(connectedCount >= 1);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Connection retry on initial connect
    // =====================================================================

    [Fact]
    public async Task ConnectRetry_FactoryFailsThenSucceeds()
    {
        int attempt = 0;
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                int current = Interlocked.Increment(ref attempt);
                if (current <= 2)
                    throw new IOException($"Connection refused (attempt {current})");
                return Task.FromResult<IStreamPair>(duplex.SideA);
            },
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            PingInterval = TimeSpan.Zero,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        var errors = new List<Exception>();
        client.OnError += ex => errors.Add(ex);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(attempt >= 3);
        Assert.True(errors.Count >= 2); // 2 failed attempts

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConnectRetry_MaxAttemptsExhausted_WaitForReadyThrows()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => throw new IOException("No network"),
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        mux.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<MultiplexerException>(
            () => mux.WaitForReadyAsync(cts.Token));

        Assert.Contains("3 attempts", ex.Message);

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task ConnectRetry_NoRetryOnInitial_WhenMaxAttemptsZero()
    {
        int attempts = 0;
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("No network");
            },
            MaxAutoReconnectAttempts = 0,
        });

        mux.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => mux.WaitForReadyAsync(cts.Token));

        Assert.Equal(1, attempts);

        await mux.DisposeAsync();
    }

    // =====================================================================
    // OnReconnecting event
    // =====================================================================

    [Fact]
    public async Task OnReconnecting_FiresOnRetry()
    {
        int attempt = 0;
        var duplex = new DuplexMemoryStream();
        var reconnectAttempts = new List<int>();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                int current = Interlocked.Increment(ref attempt);
                if (current <= 3)
                    throw new IOException("Connection refused");
                return Task.FromResult<IStreamPair>(duplex.SideA);
            },
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            PingInterval = TimeSpan.Zero,
        });

        client.OnReconnecting += n => reconnectAttempts.Add(n);

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Reconnecting fires on attempts 2, 3, 4 (first attempt has no delay/event on initial connect)
        Assert.True(reconnectAttempts.Count >= 2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Dispose during connection retry
    // =====================================================================

    [Fact]
    public async Task Dispose_DuringConnectionRetry_CleansUpGracefully()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                throw new IOException("Should not reach here");
            },
            MaxAutoReconnectAttempts = 0,
        });

        mux.Start();

        // Give it a moment to enter the factory
        await Task.Delay(50);

        // Dispose while it's waiting in StreamFactory
        await mux.DisposeAsync();

        Assert.False(mux.IsRunning);
        Assert.False(mux.IsConnected);
    }

    // =====================================================================
    // WaitForReadyAsync cancellation
    // =====================================================================

    [Fact]
    public async Task WaitForReadyAsync_CancelsCleanly()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                throw new IOException("never");
            },
            MaxAutoReconnectAttempts = 0,
        });

        mux.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mux.WaitForReadyAsync(cts.Token));

        await mux.DisposeAsync();
    }
}
