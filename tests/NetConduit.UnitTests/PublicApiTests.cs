namespace NetConduit.UnitTests;

public sealed class PublicApiTests
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

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

        string? openedId = null;
        client.ChannelOpened += (_, e) => openedId = e.ChannelId;

        client.OpenChannel("event-channel");

        Assert.Equal("event-channel", openedId);

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ch = client.OpenChannel("close-event");
        await server.AcceptChannelAsync("close-event", cts.Token);

        // OnChannelClosed fires on the receiving side when FIN arrives
        var closedTcs = new TaskCompletionSource<string>();
        server.ChannelClosed += (_, e) => closedTcs.TrySetResult(e.ChannelId);

        await ch.DisposeAsync();

        var closedId = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writeCh = client.OpenChannel("read-on-closed");
        var readCh = await server.AcceptChannelAsync("read-on-closed", cts.Token);

        var closedTcs = new TaskCompletionSource<ChannelCloseReason>();
        readCh.Closed += (_, e) => closedTcs.TrySetResult(e.Reason);

        await writeCh.DisposeAsync();

        var closeReason = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
}
