namespace NetConduit.UnitTests;

public sealed class PublicApiTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(
        DefaultChannelOptions? clientDefaultChannelOptions = null,
        DefaultChannelOptions? serverDefaultChannelOptions = null)
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = clientDefaultChannelOptions ?? new DefaultChannelOptions(),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = serverDefaultChannelOptions ?? new DefaultChannelOptions(),
        });
        return (client, server);
    }

    [Fact]
    public async Task GetWriteChannel_ExistingChannel_ReturnsChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel("lookup");
        var found = client.GetWriteChannel("lookup");

        Assert.Same(channel, found);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetWriteChannel_NonExistent_ReturnsNull()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Null(client.GetWriteChannel("nonexistent"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetReadChannel_ExistingChannel_ReturnsChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        client.OpenChannel("read-lookup");
        var accepted = await server.AcceptChannelAsync("read-lookup", cts.Token);

        var found = server.GetReadChannel("read-lookup");
        Assert.Same(accepted, found);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetReadChannel_NonExistent_ReturnsNull()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Null(server.GetReadChannel("nonexistent"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ActiveChannelIds_NoChannels_ReturnsEmpty()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Empty(client.ActiveChannelIds);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ActiveChannelIds_WithOpenChannels_ContainsIds()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        client.OpenChannel("ch-a");
        client.OpenChannel("ch-b");

        var ids = client.ActiveChannelIds;
        Assert.Contains("ch-a", ids);
        Assert.Contains("ch-b", ids);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ActiveChannelCount_TracksCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Equal(0, client.ActiveChannelCount);

        var ch1 = client.OpenChannel("count-1");
        var ch2 = client.OpenChannel("count-2");

        Assert.Equal(2, client.ActiveChannelCount);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SessionId_IsNonEmpty()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.NotEqual(Guid.Empty, client.SessionId);
        Assert.NotEqual(Guid.Empty, server.SessionId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task SessionId_DifferentBetweenPeers()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.NotEqual(client.SessionId, server.SessionId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RemoteSessionId_MatchesPeerSessionId()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Equal(client.SessionId, server.RemoteSessionId);
        Assert.Equal(server.SessionId, client.RemoteSessionId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_InitialState_AllZero()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Before any data transfer
        Assert.Equal(0, client.Stats.TotalChannelsOpened);
        Assert.Equal(0, client.Stats.TotalChannelsClosed);
        Assert.Equal(0, client.Stats.OpenChannels);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_TotalChannelsOpened_Increments()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        client.OpenChannel("stats-1");
        client.OpenChannel("stats-2");
        client.OpenChannel("stats-3");

        Assert.Equal(3, client.Stats.TotalChannelsOpened);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_Uptime_IsPositiveAfterStart()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await Task.Delay(50);
        Assert.True(client.Stats.Uptime > TimeSpan.Zero);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_BytesSentAndReceived_TrackDataTransfer()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var ch = client.OpenChannel("bytes-tracked");
        var readCh = await server.AcceptChannelAsync("bytes-tracked", cts.Token);

        await ch.WriteAsync(new byte[1024], cts.Token);
        var buf = new byte[2048];
        _ = await readCh.ReadAsync(buf, cts.Token);

        Assert.True(client.Stats.BytesSent > 0);
        Assert.True(server.Stats.BytesReceived > 0);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OnChannelOpened_EventFires()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string? openedId = null;
        bool readyAtEvent = false;
        client.ChannelOpened += (_, e) =>
        {
            openedId = e.ChannelId;
            var ch = client.GetWriteChannel(e.ChannelId);
            readyAtEvent = ch is not null && ch.IsReady;
        };

        var writeCh = client.OpenChannel("event-channel");
        await server.AcceptChannelAsync("event-channel", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);

        Assert.Equal("event-channel", openedId);
        Assert.True(readyAtEvent, "ChannelOpened must fire only after the channel is ready.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OnChannelOpened_DoesNotFire_BeforeRemoteAccept()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        bool fired = false;
        client.ChannelOpened += (_, _) => fired = true;

        var ch = client.OpenChannel("not-yet-accepted");

        Assert.False(ch.IsReady);
        Assert.False(fired, "ChannelOpened must not fire before the remote acknowledges the channel.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_Uptime_ReflectsElapsedMilliseconds()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await Task.Delay(500);

        var uptime = client.Stats.Uptime;
        Assert.True(uptime >= TimeSpan.FromMilliseconds(400),
            $"Stats.Uptime ({uptime.TotalMilliseconds:F3} ms) is far below actual elapsed time.");
        Assert.True(uptime < TimeSpan.FromMinutes(1),
            $"Stats.Uptime ({uptime}) is implausibly large.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OnChannelClosed_EventFires()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var ch = client.OpenChannel("close-event");
        await server.AcceptChannelAsync("close-event", cts.Token);

        // OnChannelClosed fires on the receiving side when FIN arrives
        var closedTcs = new TaskCompletionSource<string>();
        server.ChannelClosed += (_, e) => closedTcs.TrySetResult(e.ChannelId);

        await ch.DisposeAsync();

        var closedId = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("close-event", closedId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_StreamProperties_Correct()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("stream-props");
        var stream = ch.AsStream();

        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_StreamProperties_Correct()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        client.OpenChannel("read-props");
        var readCh = await server.AcceptChannelAsync("read-props", cts.Token);
        var stream = readCh.AsStream();

        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_Properties_SetCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "prop-check",
            Priority = ChannelPriority.High,
        });

        // Accept on server side so the init-ack flows back
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await server.AcceptChannelAsync("prop-check", cts.Token);
        await ch.WaitForReadyAsync(cts.Token);

        Assert.Equal("prop-check", ch.ChannelId);
        Assert.Equal(ChannelState.Open, ch.State);
        Assert.Equal(ChannelPriority.High, ch.Priority);
        Assert.NotNull(ch.Stats);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_Properties_SetCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        client.OpenChannel("read-props-2");
        var readCh = await server.AcceptChannelAsync("read-props-2", cts.Token);

        Assert.Equal("read-props-2", readCh.ChannelId);
        Assert.Equal(ChannelState.Open, readCh.State);
        Assert.NotNull(readCh.Stats);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_AfterClose_StateIsClosed()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("state-check");
        // Accept on server so init-ack flows back
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await server.AcceptChannelAsync("state-check", cts.Token);
        await ch.WaitForReadyAsync(cts.Token);
        Assert.Equal(ChannelState.Open, ch.State);

        await ch.DisposeAsync();

        Assert.True(ch.State is ChannelState.Closing or ChannelState.Closed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_OnClosed_FiresOnDispose()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("on-closed");
        ChannelCloseReason? reason = null;
        ch.Closed += (_, e) => reason = e.Reason;

        await ch.DisposeAsync();

        Assert.Equal(ChannelCloseReason.LocalClose, reason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_OnClosed_Fires()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var writeCh = client.OpenChannel("read-on-closed");
        var readCh = await server.AcceptChannelAsync("read-on-closed", cts.Token);

        var closedTcs = new TaskCompletionSource<ChannelCloseReason>();
        readCh.Closed += (_, e) => closedTcs.TrySetResult(e.Reason);

        await writeCh.DisposeAsync();

        var closeReason = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ChannelCloseReason.RemoteFin, closeReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelStats_TrackBytesAndFrames()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var writeCh = client.OpenChannel("stats-channel");
        var readCh = await server.AcceptChannelAsync("stats-channel", cts.Token);

        await writeCh.WriteAsync(new byte[100], cts.Token);
        await writeCh.WriteAsync(new byte[200], cts.Token);

        // Read all
        var buf = new byte[1024];
        int total = 0;
        while (total < 300)
        {
            int r = await readCh.ReadAsync(buf.AsMemory(total), cts.Token);
            if (r == 0) break;
            total += r;
        }

        Assert.True(writeCh.Stats.BytesSent >= 300);
        Assert.True(writeCh.Stats.FramesSent >= 2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =================================================================
    // Interface contracts — return types are interfaces, not concrete
    // =================================================================

    [Fact]
    public async Task OpenChannel_ReturnsIWriteChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("iface-write");

        Assert.IsAssignableFrom<IWriteChannel>(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_ReturnsIReadChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        client.OpenChannel("iface-read");
        var ch = server.AcceptChannel("iface-read");

        Assert.IsAssignableFrom<IReadChannel>(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelsAsync_YieldsIReadChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("yield-test");

        await foreach (var ch in server.AcceptChannelsAsync(ct: cts.Token))
        {
            Assert.IsAssignableFrom<IReadChannel>(ch);
            break;
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetWriteChannel_ReturnsIWriteChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        client.OpenChannel("get-iface");
        var found = client.GetWriteChannel("get-iface");

        Assert.IsAssignableFrom<IWriteChannel>(found);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetReadChannel_ReturnsIReadChannel()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("get-read-iface");
        await server.AcceptChannelAsync("get-read-iface", cts.Token);
        var found = server.GetReadChannel("get-read-iface");

        Assert.IsAssignableFrom<IReadChannel>(found);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =================================================================
    // AsStream() — stream interop
    // =================================================================

    [Fact]
    public async Task WriteChannel_AsStream_ReturnsSameInstance()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("as-stream-identity");
        var s1 = ch.AsStream();
        var s2 = ch.AsStream();

        Assert.Same(s1, s2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_AsStream_ReturnsSameInstance()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("as-stream-read-id");
        var readCh = await server.AcceptChannelAsync("as-stream-read-id", cts.Token);
        var s1 = readCh.AsStream();
        var s2 = readCh.AsStream();

        Assert.Same(s1, s2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AsStream_WriteAndRead_DataFlowsCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writeCh = client.OpenChannel("stream-data-flow");
        var readCh = await server.AcceptChannelAsync("stream-data-flow", cts.Token);

        var writeStream = writeCh.AsStream();
        var readStream = readCh.AsStream();

        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await writeStream.WriteAsync(data, cts.Token);

        var buf = new byte[16];
        int read = await readStream.ReadAsync(buf, cts.Token);

        Assert.Equal(4, read);
        Assert.Equal(data, buf[..4]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AsStream_ReadExactlyAsync_WorksForFixedProtocol()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var writeCh = client.OpenChannel("stream-read-exactly");
        var readCh = await server.AcceptChannelAsync("stream-read-exactly", cts.Token);

        var header = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        await writeCh.WriteAsync(header, cts.Token);

        var readStream = readCh.AsStream();
        var buf = new byte[4];
        await readStream.ReadExactlyAsync(buf, cts.Token);

        Assert.Equal(header, buf);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =================================================================
    // Extension methods — OpenChannel(string), OpenChannelAsync, AcceptChannelAsync
    // =================================================================

    [Fact]
    public async Task OpenChannel_StringExtension_CreatesChannelWithCorrectId()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("ext-test");

        Assert.Equal("ext-test", ch.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_StringExtension_UsesDefaultPriority()
    {
        var (client, server) = CreatePair(clientDefaultChannelOptions: new DefaultChannelOptions
        {
            Priority = ChannelPriority.Highest,
            SlabSize = 128 * 1024,
            SendTimeout = TimeSpan.FromMilliseconds(1234),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("ext-defaults");

        Assert.Equal(ChannelPriority.Highest, ch.Priority);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_WaitsForReady()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        server.AcceptChannel("async-open");
        var ch = await client.OpenChannelAsync("async-open", cts.Token);

        Assert.True(ch.IsReady);
        Assert.Equal(ChannelState.Open, ch.State);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_WithOptions_WaitsForReady()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        server.AcceptChannel("async-opts");
        var ch = await client.OpenChannelAsync(
            new ChannelOptions { ChannelId = "async-opts", Priority = ChannelPriority.High },
            cts.Token);

        Assert.True(ch.IsReady);
        Assert.Equal(ChannelPriority.High, ch.Priority);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelAsync_Extension_WaitsForReady()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("accept-ext");
        var readCh = await server.AcceptChannelAsync("accept-ext", cts.Token);

        Assert.True(readCh.IsReady);
        Assert.Equal("accept-ext", readCh.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_IsConnected_TrueWhenTransportActive()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        server.AcceptChannel("conn-check");
        var ch = await client.OpenChannelAsync("conn-check", cts.Token);

        Assert.True(ch.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_IsConnected_TrueWhenTransportActive()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("read-conn");
        var readCh = await server.AcceptChannelAsync("read-conn", cts.Token);

        Assert.True(readCh.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_CloseAsync_ClosesGracefully()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        server.AcceptChannel("close-async");
        var ch = await client.OpenChannelAsync("close-async", cts.Token);

        await ch.CloseAsync(cts.Token);

        Assert.True(ch.State is ChannelState.Closing or ChannelState.Closed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_CloseAsync_ClosesGracefully()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        client.OpenChannel("read-close-async");
        var readCh = await server.AcceptChannelAsync("read-close-async", cts.Token);

        await readCh.CloseAsync(cts.Token);

        Assert.True(readCh.State is ChannelState.Closing or ChannelState.Closed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public void CreateOptions_AutoReconnectBackoffMultiplier_Infinity_Throws()
    {
        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            AutoReconnectBackoffMultiplier = double.PositiveInfinity,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(opts));
        Assert.Contains("AutoReconnectBackoffMultiplier", ex.Message);
    }
}
