namespace NetConduit.UnitTests;

/// <summary>
/// Tests that verify behavior AFTER a transport dies and reconnects:
/// - Data written before disconnect arrives after reconnect
/// - Channels survive reconnection
/// - Events fire correctly through the lifecycle
/// - Multiple reconnects in sequence work
/// </summary>
public sealed class AfterReconnectionTests : IAsyncDisposable
{
    private readonly ReconnectableTransportFactory _factory = new();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    // =====================================================================
    // After reconnect, OnConnected fires again
    // =====================================================================

    [Fact]
    public async Task Reconnect_OnConnectedFiresAgain()
    {
        int connectedCount = 0;
        var secondConnect = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        client.OnConnected += () =>
        {
            var count = Interlocked.Increment(ref connectedCount);
            if (count >= 2) secondConnect.TrySetResult();
        };

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        Assert.Equal(1, connectedCount);

        // Kill transport — both sides should reconnect
        _factory.KillCurrentTransport();

        // Wait for second connect
        await secondConnect.Task.WaitAsync(cts.Token);
        Assert.True(connectedCount >= 2);
        Assert.True(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Data written before disconnect is replayed after reconnect
    // =====================================================================

    [Fact]
    public async Task Reconnect_DataWrittenBeforeDisconnect_ArrivesAfterReconnect()
    {
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Open channel and write data
        var wch = client.OpenChannel("data");
        byte[] testData = [0xDE, 0xAD, 0xBE, 0xEF];
        await wch.WriteAsync(testData);

        // Accept and read the data before disconnect
        var rch = await server.AcceptChannelAsync("data", cts.Token);
        byte[] buf = new byte[4];
        int read = await rch.ReadAsync(buf, cts.Token);
        Assert.Equal(4, read);
        Assert.Equal(testData, buf);

        // Now kill transport and wait for reconnect
        var reconnectedTcs = new TaskCompletionSource();
        int connCount = 0;
        client.OnConnected += () =>
        {
            if (Interlocked.Increment(ref connCount) >= 1)
                reconnectedTcs.TrySetResult();
        };
        _factory.KillCurrentTransport();
        await reconnectedTcs.Task.WaitAsync(cts.Token);

        // Write more data after reconnect — it should flow through
        byte[] moreData = [0xCA, 0xFE];
        await wch.WriteAsync(moreData);

        // After reconnect, unACK'd data is replayed first (small payloads never hit
        // the 25% ACK threshold), then new data follows. Read all and verify new data
        // is present at the end.
        byte[] all = new byte[64];
        int totalRead = 0;
        while (totalRead < testData.Length + moreData.Length)
        {
            int r = await rch.ReadAsync(all.AsMemory(totalRead), cts.Token);
            if (r == 0) break;
            totalRead += r;
        }
        // New data appears after replayed data
        Assert.True(totalRead >= testData.Length + moreData.Length);
        Assert.Equal(moreData, all.AsSpan(totalRead - moreData.Length, moreData.Length).ToArray());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Channel survives reconnection — can still write/read
    // =====================================================================

    [Fact]
    public async Task Reconnect_ExistingChannel_SurvivesAndContinuesWorking()
    {
        var reconnectedClient = new TaskCompletionSource();
        var reconnectedServer = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Open channel
        var wch = client.OpenChannel("survive");
        var rch = await server.AcceptChannelAsync("survive", cts.Token);

        // Setup reconnect detection
        bool clientReconnected = false;
        bool serverReconnected = false;
        client.OnConnected += () =>
        {
            clientReconnected = true;
            reconnectedClient.TrySetResult();
        };
        server.OnConnected += () =>
        {
            serverReconnected = true;
            reconnectedServer.TrySetResult();
        };

        // Kill transport
        _factory.KillCurrentTransport();

        // Wait for both to reconnect
        await reconnectedClient.Task.WaitAsync(cts.Token);
        await reconnectedServer.Task.WaitAsync(cts.Token);

        Assert.True(clientReconnected);
        Assert.True(serverReconnected);

        // Write new data after reconnect
        byte[] newData = [42, 43, 44];
        await wch.WriteAsync(newData);

        byte[] received = new byte[3];
        int totalRead = 0;
        while (totalRead < 3)
        {
            int r = await rch.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (r == 0) break;
            totalRead += r;
        }
        Assert.Equal(3, totalRead);
        Assert.Equal(newData, received);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Multiple channels survive reconnection
    // =====================================================================

    [Fact]
    public async Task Reconnect_MultipleChannels_AllSurvive()
    {
        var reconnectedClient = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Open 3 channels
        var wChannels = new WriteChannel[3];
        var rChannels = new ReadChannel[3];
        for (int i = 0; i < 3; i++)
        {
            wChannels[i] = client.OpenChannel($"multi-{i}");
            rChannels[i] = await server.AcceptChannelAsync($"multi-{i}", cts.Token);
        }

        // Write to each before disconnect
        for (int i = 0; i < 3; i++)
            await wChannels[i].WriteAsync(new byte[] { (byte)(i + 10) });

        // Read from each before disconnect
        for (int i = 0; i < 3; i++)
        {
            byte[] b = new byte[1];
            int r = await rChannels[i].ReadAsync(b, cts.Token);
            Assert.Equal(1, r);
            Assert.Equal((byte)(i + 10), b[0]);
        }

        // Kill and reconnect
        client.OnConnected += () => reconnectedClient.TrySetResult();
        _factory.KillCurrentTransport();
        await reconnectedClient.Task.WaitAsync(cts.Token);

        // Write new data to all channels after reconnect
        for (int i = 0; i < 3; i++)
            await wChannels[i].WriteAsync(new byte[] { (byte)(i + 100) });

        // After reconnect, unACK'd data replays first (1 byte per channel),
        // then new data follows. Read past replay to find new data.
        for (int i = 0; i < 3; i++)
        {
            byte[] all = new byte[16];
            int totalRead = 0;
            // Need at least 2 bytes: 1 replayed + 1 new
            while (totalRead < 2)
            {
                int r = await rChannels[i].ReadAsync(all.AsMemory(totalRead), cts.Token);
                if (r == 0) break;
                totalRead += r;
            }
            Assert.True(totalRead >= 2);
            // New data is at the end
            Assert.Equal((byte)(i + 100), all[totalRead - 1]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Multiple sequential reconnections work
    // =====================================================================

    [Fact]
    public async Task Reconnect_MultipleSequentialReconnections_AllSucceed()
    {
        int connectedCount = 0;
        var thirdConnect = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        client.OnConnected += () =>
        {
            var c = Interlocked.Increment(ref connectedCount);
            if (c >= 3) thirdConnect.TrySetResult();
        };

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // First reconnect
        var secondConnect = new TaskCompletionSource();
        client.OnConnected += () =>
        {
            if (Volatile.Read(ref connectedCount) >= 2) secondConnect.TrySetResult();
        };
        _factory.KillCurrentTransport();
        await secondConnect.Task.WaitAsync(cts.Token);

        // Verify connected
        Assert.True(client.IsConnected);

        // Second reconnect
        _factory.KillCurrentTransport();
        await thirdConnect.Task.WaitAsync(cts.Token);

        Assert.True(client.IsConnected);
        Assert.True(connectedCount >= 3);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // OnReconnecting fires with correct attempt count
    // =====================================================================

    [Fact]
    public async Task Reconnect_OnReconnectingFires_WithAttemptNumber()
    {
        var attempts = new List<int>();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        client.OnReconnecting += attempt => { lock (attempts) { attempts.Add(attempt); } };

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Register AFTER initial connect so TCS fires on reconnect only
        var reconnected = new TaskCompletionSource();
        client.OnConnected += () => reconnected.TrySetResult();

        // Kill transport — reconnect should fire
        _factory.KillCurrentTransport();
        await reconnected.Task.WaitAsync(cts.Token);

        // OnReconnecting should have fired at least once with attempt=1
        lock (attempts) { Assert.Contains(1, attempts); }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // ConnectionCount tracks total connections through factory
    // =====================================================================

    [Fact]
    public async Task Reconnect_ConnectionCount_IncrementsOnReconnect()
    {
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        int initialCount = _factory.ConnectionCount;

        // Register AFTER initial connect so TCS fires on reconnect only
        var reconnected = new TaskCompletionSource();
        client.OnConnected += () => reconnected.TrySetResult();

        _factory.KillCurrentTransport();
        await reconnected.Task.WaitAsync(cts.Token);

        Assert.True(_factory.ConnectionCount > initialCount);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
