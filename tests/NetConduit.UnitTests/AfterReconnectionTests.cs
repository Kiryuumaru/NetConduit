namespace NetConduit.UnitTests;

/// <summary>
/// Tests that verify behavior AFTER a transport dies and reconnects:
/// - Data written before disconnect arrives after reconnect
/// - Channels survive reconnection
/// - Events fire correctly through the lifecycle
/// - Multiple reconnects in sequence work
/// </summary>
[Collection("Sequential")]
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
        client.Connected += (_, _) =>
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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
        var reconnectedClient = new TaskCompletionSource();
        var reconnectedServer = new TaskCompletionSource();
        client.Connected += (_, _) => reconnectedClient.TrySetResult();
        server.Connected += (_, _) => reconnectedServer.TrySetResult();
        _factory.KillCurrentTransport();
        await Task.WhenAll(
            reconnectedClient.Task.WaitAsync(cts.Token),
            reconnectedServer.Task.WaitAsync(cts.Token));

        // Write more data after reconnect — it should flow through
        byte[] moreData = [0xCA, 0xFE];
        await wch.WriteAsync(moreData);

        // After reconnect, previously-received data is NOT replayed to the reader.
        // Only new data arrives.
        byte[] all = new byte[64];
        int totalRead = 0;
        while (totalRead < moreData.Length)
        {
            int r = await rch.ReadAsync(all.AsMemory(totalRead), cts.Token);
            if (r == 0) break;
            totalRead += r;
        }
        Assert.Equal(moreData.Length, totalRead);
        Assert.Equal(moreData, all.AsSpan(0, moreData.Length).ToArray());

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Open channel
        var wch = client.OpenChannel("survive");
        var rch = await server.AcceptChannelAsync("survive", cts.Token);

        // Setup reconnect detection
        bool clientReconnected = false;
        bool serverReconnected = false;
        client.Connected += (_, _) =>
        {
            clientReconnected = true;
            reconnectedClient.TrySetResult();
        };
        server.Connected += (_, _) =>
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Open 3 channels
        var wChannels = new IWriteChannel[3];
        var rChannels = new IReadChannel[3];
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
        var reconnectedServer = new TaskCompletionSource();
        client.Connected += (_, _) => reconnectedClient.TrySetResult();
        server.Connected += (_, _) => reconnectedServer.TrySetResult();
        _factory.KillCurrentTransport();
        await Task.WhenAll(
            reconnectedClient.Task.WaitAsync(cts.Token),
            reconnectedServer.Task.WaitAsync(cts.Token));

        // Write new data to all channels after reconnect
        for (int i = 0; i < 3; i++)
            await wChannels[i].WriteAsync(new byte[] { (byte)(i + 100) });

        // After reconnect, previously-received data is NOT replayed.
        // Only new data arrives.
        for (int i = 0; i < 3; i++)
        {
            byte[] b = new byte[1];
            int r = await rChannels[i].ReadAsync(b, cts.Token);
            Assert.Equal(1, r);
            Assert.Equal((byte)(i + 100), b[0]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Regression for issue #161: when the reader has crossed the ACK threshold
    // (so the writer's _ackedPos was advanced via OnAck), reconnect must still
    // not duplicate-deliver any of the previously-acked bytes. Exercises the
    // new reconnect-handshake replay-base advertisement path together with the
    // existing OnAck path; with the old code, an in-flight ACK lost on the
    // failing transport would leave writer._ackedPos behind reader._frameBytesReceived
    // and the post-reconnect replay would duplicate the gap into ReadAsync.
    // =====================================================================

    [Fact]
    public async Task Reconnect_AfterAckThresholdCrossed_NoDuplicateDelivery()
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Use the minimum slab (64 KiB) so the ACK threshold (slabSize / 16 = 4 KiB)
        // is comfortably exceeded by the first batch without exhausting the slab.
        var wch = client.OpenChannel("bulk");
        var rch = await server.AcceptChannelAsync("bulk", cts.Token);
        await wch.WaitForReadyAsync(cts.Token);

        // Write enough to push the reader well past the 4 KiB ACK threshold.
        byte[] preData = new byte[16 * 1024];
        for (int i = 0; i < preData.Length; i++) preData[i] = (byte)(i & 0xFF);
        await wch.WriteAsync(preData);

        byte[] preBuf = new byte[preData.Length];
        int preRead = 0;
        while (preRead < preData.Length)
        {
            int r = await rch.ReadAsync(preBuf.AsMemory(preRead), cts.Token);
            if (r == 0) break;
            preRead += r;
        }
        Assert.Equal(preData.Length, preRead);
        Assert.Equal(preData, preBuf);

        // Give the writer-side mux time to process the ACK frames the reader emitted.
        // 50 ms is comfortable for the in-process DuplexMemoryStream transport.
        await Task.Delay(50, cts.Token);

        // Kill the transport and wait for symmetric reconnect.
        var reconnectedClient = new TaskCompletionSource();
        var reconnectedServer = new TaskCompletionSource();
        client.Connected += (_, _) => reconnectedClient.TrySetResult();
        server.Connected += (_, _) => reconnectedServer.TrySetResult();
        _factory.KillCurrentTransport();
        await Task.WhenAll(
            reconnectedClient.Task.WaitAsync(cts.Token),
            reconnectedServer.Task.WaitAsync(cts.Token));

        // After reconnect, only post-reconnect bytes must arrive — no replayed prefix.
        byte[] postData = [0xAA, 0xBB, 0xCC, 0xDD];
        await wch.WriteAsync(postData);

        byte[] postBuf = new byte[postData.Length];
        int postRead = 0;
        while (postRead < postData.Length)
        {
            int r = await rch.ReadAsync(postBuf.AsMemory(postRead), cts.Token);
            if (r == 0) break;
            postRead += r;
        }
        Assert.Equal(postData.Length, postRead);
        Assert.Equal(postData, postBuf);

        // No extra bytes should be queued — a duplicate-replay would land here.
        using var spyCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        spyCts.CancelAfter(TimeSpan.FromMilliseconds(100));
        byte[] spy = new byte[16];
        try
        {
            int extra = await rch.ReadAsync(spy, spyCts.Token);
            Assert.Equal(0, extra);
        }
        catch (OperationCanceledException)
        {
            // Expected: no further data ever arrives → ReadAsync stayed blocked.
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
        int clientConnectedCount = 0;
        int serverConnectedCount = 0;
        var clientReconnect2 = new TaskCompletionSource();
        var serverReconnect2 = new TaskCompletionSource();
        var clientReconnect3 = new TaskCompletionSource();
        var serverReconnect3 = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        client.Connected += (_, _) =>
        {
            var c = Interlocked.Increment(ref clientConnectedCount);
            if (c >= 2) clientReconnect2.TrySetResult();
            if (c >= 3) clientReconnect3.TrySetResult();
        };

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        server.Connected += (_, _) =>
        {
            var c = Interlocked.Increment(ref serverConnectedCount);
            if (c >= 2) serverReconnect2.TrySetResult();
            if (c >= 3) serverReconnect3.TrySetResult();
        };

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // First reconnect — wait for BOTH sides to be fully reconnected before
        // killing again. Waiting on only the client side races against the server
        // still wiring up its IO loops, which can leave the next kill targeting a
        // partially-initialized transport and stall the second reconnect.
        _factory.KillCurrentTransport();
        await Task.WhenAll(
            clientReconnect2.Task.WaitAsync(cts.Token),
            serverReconnect2.Task.WaitAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        // Second reconnect
        _factory.KillCurrentTransport();
        await Task.WhenAll(
            clientReconnect3.Task.WaitAsync(cts.Token),
            serverReconnect3.Task.WaitAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
        Assert.True(clientConnectedCount >= 3);
        Assert.True(serverConnectedCount >= 3);

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
        client.Reconnecting += (_, e) => { lock (attempts) { attempts.Add(e.Attempt); } };

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Register AFTER initial connect so TCS fires on reconnect only
        var reconnected = new TaskCompletionSource();
        client.Connected += (_, _) => reconnected.TrySetResult();

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        int initialCount = _factory.ConnectionCount;

        // Register AFTER initial connect so TCS fires on reconnect only
        var reconnected = new TaskCompletionSource();
        client.Connected += (_, _) => reconnected.TrySetResult();

        _factory.KillCurrentTransport();
        await reconnected.Task.WaitAsync(cts.Token);

        Assert.True(_factory.ConnectionCount > initialCount);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
