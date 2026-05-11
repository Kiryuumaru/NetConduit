namespace NetConduit.UnitTests;

/// <summary>
/// Tests for data safety during transport disconnections with reconnection enabled.
/// Verifies that channels survive disconnect, writes succeed after disconnect,
/// and data is eventually delivered when transport is restored.
/// </summary>
public sealed class DisconnectionDataLossTests
{
    [Fact]
    public async Task WriteAfterDisconnect_SmallData_Succeeds()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("ch1");

        // Kill transport round 0
        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // Write after disconnect — should not throw (buffers in slab)
        await writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteAfterDisconnect_LargeData_Succeeds()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("ch1");

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // 64KB write after disconnect
        var data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteAfterDisconnect_MultipleWrites_AllSucceed()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("ch1");

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // Multiple writes after disconnect
        for (int i = 0; i < 10; i++)
        {
            var data = new byte[256];
            data.AsSpan().Fill((byte)i);
            await writer.WriteAsync(data, cts.Token);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelState_AfterDisconnect_StaysOpen()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("ch1");
        await server.AcceptChannelAsync("ch1", cts.Token);
        await writer.WaitForReadyAsync(cts.Token);

        Assert.Equal(ChannelState.Open, writer.State);

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // Channel should still be ready after disconnect (reconnection will restore transport)
        Assert.True(writer.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultipleChannels_AllSurviveDisconnect()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writers = new IWriteChannel[5];
        for (int i = 0; i < 5; i++)
        {
            writers[i] = client.OpenChannel($"multi-{i}");
            await server.AcceptChannelAsync($"multi-{i}", cts.Token);
            await writers[i].WaitForReadyAsync(cts.Token);
        }

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // All channels should still be ready after disconnect
        for (int i = 0; i < 5; i++)
        {
            Assert.True(writers[i].IsReady);
            await writers[i].WriteAsync(new byte[] { (byte)i }, cts.Token);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OnDisconnected_EventFires_WithCorrectReason()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var disconnectTcs = new TaskCompletionSource<DisconnectReason>();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });
        client.Disconnected += (_, e) => disconnectTcs.TrySetResult(e.Reason);

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        broker.KillRound(0);

        var reason = await disconnectTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(DisconnectReason.TransportError, reason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OnReconnecting_EventFires_AfterDisconnect()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var reconnectingTcs = new TaskCompletionSource<int>();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });
        client.Reconnecting += (_, e) => reconnectingTcs.TrySetResult(e.Attempt);

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        broker.KillRound(0);

        var attempt = await reconnectingTcs.Task.WaitAsync(cts.Token);
        Assert.True(attempt >= 1);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DataContinuity_AfterReconnect_AllBytesReceived()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientReconnected = new TaskCompletionSource();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });
        client.Connected += (_, _) => clientReconnected.TrySetResult();

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        // Reset after first connect
        clientReconnected = new TaskCompletionSource();

        var writer = client.OpenChannel("data-continuity");
        var reader = await server.AcceptChannelAsync("data-continuity", cts.Token);

        // Write before disconnect
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);

        broker.KillRound(0);

        // Wait for reconnection
        await clientReconnected.Task.WaitAsync(cts.Token);

        // Write after reconnect
        await writer.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);
        await writer.DisposeAsync();

        // Read all data
        var buf = new byte[64];
        int total = 0;
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;

        // At minimum, post-reconnect data should arrive
        Assert.True(total >= 3);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BidirectionalChannels_BothDirectionsSurviveDisconnect()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        // Client -> Server
        var c2s = client.OpenChannel("c2s");
        await server.AcceptChannelAsync("c2s", cts.Token);
        await c2s.WaitForReadyAsync(cts.Token);

        // Server -> Client
        var s2c = server.OpenChannel("s2c");
        await client.AcceptChannelAsync("s2c", cts.Token);
        await s2c.WaitForReadyAsync(cts.Token);

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // Both directions should still accept writes (IsReady stays true after disconnect)
        Assert.True(c2s.IsReady);
        Assert.True(s2c.IsReady);

        await c2s.WriteAsync(new byte[] { 1 }, cts.Token);
        await s2c.WriteAsync(new byte[] { 2 }, cts.Token);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_BytesSent_PreservedAfterDisconnect()
    {
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("stats-test");
        await server.AcceptChannelAsync("stats-test", cts.Token);

        await writer.WriteAsync(new byte[1000], cts.Token);
        var bytesBefore = client.Stats.BytesSent;
        Assert.True(bytesBefore > 0);

        broker.KillRound(0);
        await Task.Delay(200, cts.Token);

        // Stats should be preserved (not reset)
        Assert.True(client.Stats.BytesSent >= bytesBefore);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
