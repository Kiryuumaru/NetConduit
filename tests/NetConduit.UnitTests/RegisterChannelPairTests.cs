namespace NetConduit.UnitTests;

public sealed class RegisterChannelPairTests
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

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> StartedPairAsync()
    {
        var (c, s) = CreatePair();
        c.Start(); s.Start();
        await Task.WhenAll(c.WaitForReadyAsync(), s.WaitForReadyAsync());
        return (c, s);
    }

    [Fact]
    public async Task RegisterChannelPair_HappyPath_RegistersWriteAndRead()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var (write, read) = client.RegisterChannelPair("chat>>", "chat<<");

        Assert.Equal("chat>>", write.ChannelId);
        Assert.Equal("chat<<", read.ChannelId);
        Assert.Same(write, client.GetWriteChannel("chat>>"));
        Assert.Same(read, client.AcceptChannel("chat<<"));
    }

    [Fact]
    public async Task RegisterChannelPair_WriteIdCollision_ThrowsChannelExists()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var preExisting = client.OpenChannel("taken>>");
        Assert.NotNull(preExisting);

        var ex = Assert.Throws<MultiplexerException>(() =>
            client.RegisterChannelPair("taken>>", "free<<"));
        Assert.Equal(ErrorCode.ChannelExists, ex.ErrorCode);

        Assert.Same(preExisting, client.GetWriteChannel("taken>>"));
        Assert.Null(client.GetReadChannel("free<<"));
    }

    [Fact]
    public async Task RegisterChannelPair_ReadIdCollision_RollsBackWrite()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        // Pre-occupy the read id with an outbound binding so the pair
        // collides on its second registration.
        var preExisting = client.OpenChannel("rx<<");
        Assert.NotNull(preExisting);

        var ex = Assert.Throws<MultiplexerException>(() =>
            client.RegisterChannelPair("tx>>", "rx<<"));
        Assert.Equal(ErrorCode.ChannelExists, ex.ErrorCode);

        // The first registration must have been rolled back.
        Assert.Same(preExisting, client.GetWriteChannel("rx<<"));
        Assert.Null(client.GetWriteChannel("tx>>"));

        // The rolled-back outbound sent no INIT — the server never sees it.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.AcceptChannelAsync("tx>>", cts.Token).AsTask());

        // The slot is reusable.
        var (write, _) = client.RegisterChannelPair("tx>>", "fresh<<");
        Assert.NotNull(write);
    }

    [Fact]
    public async Task RegisterChannelPair_InvalidId_PropagatesArgumentException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        Assert.Throws<ArgumentException>(() =>
            client.RegisterChannelPair("", "ok<<"));

        // Up-front validation: nothing was committed.
        Assert.Null(client.GetReadChannel("ok<<"));
    }

    [Fact]
    public async Task RegisterChannelPair_NullWriteId_ThrowsArgumentException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        Assert.Throws<ArgumentException>(() =>
            client.RegisterChannelPair(null!, "ok<<"));

        Assert.Null(client.GetReadChannel("ok<<"));
    }

    [Fact]
    public async Task RegisterChannelPair_EmptyReadId_ThrowsArgumentException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        Assert.Throws<ArgumentException>(() =>
            client.RegisterChannelPair("ok>>", ""));

        Assert.Null(client.GetWriteChannel("ok>>"));
    }

    [Fact]
    public async Task RegisterChannelPair_OverlongId_ThrowsArgumentException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        string overMaxId = new('x', 1025);

        Assert.Throws<ArgumentException>(() =>
            client.RegisterChannelPair(overMaxId, "ok<<"));

        Assert.Null(client.GetReadChannel("ok<<"));
    }

    [Fact]
    public async Task TryRegisterChannels_SlabSizeBelowMin_ThrowsArgumentOutOfRangeException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var reg = new ChannelRegistration("slab", ChannelDirection.Outbound)
        {
            Options = new ChannelOptions { ChannelId = "slab", SlabSize = 64 * 1024 - 1 },
        };
        ChannelRegistration[] regs = [reg];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.TryRegisterChannels(regs, out _));

        Assert.Null(client.GetWriteChannel("slab"));
    }

    [Fact]
    public async Task TryRegisterChannels_SlabSizeAboveMax_ThrowsArgumentOutOfRangeException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var reg = new ChannelRegistration("slab", ChannelDirection.Outbound)
        {
            Options = new ChannelOptions { ChannelId = "slab", SlabSize = 64 * 1024 * 1024 + 1 },
        };
        ChannelRegistration[] regs = [reg];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.TryRegisterChannels(regs, out _));

        Assert.Null(client.GetWriteChannel("slab"));
    }

    [Fact]
    public async Task TryRegisterChannels_NegativeSendTimeout_ThrowsArgumentOutOfRangeException()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var reg = new ChannelRegistration("timeout", ChannelDirection.Outbound)
        {
            Options = new ChannelOptions { ChannelId = "timeout", SendTimeout = TimeSpan.FromSeconds(-5) },
        };
        ChannelRegistration[] regs = [reg];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.TryRegisterChannels(regs, out _));

        Assert.Null(client.GetWriteChannel("timeout"));
    }

    [Fact]
    public async Task RegisterChannelPair_MatchesDirectBatchRegistration()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        var (write, read) = client.RegisterChannelPair("eq>>", "eq<<");

        // The mirror batch on the peer: outbound for the ids this side
        // accepts, inbound for the ids this side opened. The inbound entry
        // idempotently adopts the INIT that the pair above already sent.
        var serverWriteReg = new ChannelRegistration("eq<<", ChannelDirection.Outbound);
        var serverReadReg = new ChannelRegistration("eq>>", ChannelDirection.Inbound);
        ReadOnlySpan<ChannelRegistration> regs = [serverWriteReg, serverReadReg];

        Assert.True(server.TryRegisterChannels(regs, out var dict));
        Assert.Equal(2, dict.Count);
        Assert.IsAssignableFrom<IWriteChannel>(dict[serverWriteReg]);
        Assert.IsAssignableFrom<IReadChannel>(dict[serverReadReg]);
        Assert.Same(dict[serverWriteReg], server.GetWriteChannel("eq<<"));

        // Same shape on the pair side.
        Assert.IsAssignableFrom<IWriteChannel>(write);
        Assert.IsAssignableFrom<IReadChannel>(read);
    }
}
