namespace NetConduit.UnitTests;

public sealed class TryRegisterChannelsTests
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
    public async Task TryRegisterChannels_HappyPath_RegistersOutboundAndInboundAtomically()
    {
        var (client, server) = await StartedPairAsync();

        var writeReg = new ChannelRegistration("pair>>", ChannelDirection.Outbound);
        var readReg = new ChannelRegistration("pair<<", ChannelDirection.Inbound);
        ReadOnlySpan<ChannelRegistration> regs = [writeReg, readReg];

        Assert.True(client.TryRegisterChannels(regs, out var dict));
        Assert.Equal(2, dict.Count);
        Assert.IsAssignableFrom<IWriteChannel>(dict[writeReg]);
        Assert.IsAssignableFrom<IReadChannel>(dict[readReg]);
        Assert.Same(dict[writeReg], client.GetWriteChannel("pair>>"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_EmptyBatch_ThrowsArgumentException()
    {
        var (client, server) = await StartedPairAsync();

        bool threw = false;
        try { _ = client.TryRegisterChannels(ReadOnlySpan<ChannelRegistration>.Empty, out _); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_DuplicateInBatch_ReturnsFalse()
    {
        var (client, server) = await StartedPairAsync();

        var dup = new ChannelRegistration("dup", ChannelDirection.Outbound);
        ChannelRegistration[] regs = [dup, dup];

        Assert.False(client.TryRegisterChannels(regs, out _));

        // Nothing was committed.
        Assert.Null(client.GetWriteChannel("dup"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_OutboundCollision_ReturnsFalseWithNoPartialCommit()
    {
        var (client, server) = await StartedPairAsync();

        // Pre-occupy "taken" as an outbound channel.
        var preExisting = client.OpenChannel("taken");
        Assert.NotNull(preExisting);

        var freshReg = new ChannelRegistration("fresh", ChannelDirection.Outbound);
        var collidingReg = new ChannelRegistration("taken", ChannelDirection.Outbound);
        ReadOnlySpan<ChannelRegistration> regs = [freshReg, collidingReg];

        Assert.False(client.TryRegisterChannels(regs, out var dict));
        Assert.Null(dict);

        // "fresh" must have been rolled back — no leaked partial state.
        Assert.Null(client.GetWriteChannel("fresh"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_InboundIdempotent_AdoptsExistingPendingAccept()
    {
        var (client, server) = await StartedPairAsync();

        // First, create a pending accept the old way.
        var first = client.AcceptChannel("inbound1");

        var reg = new ChannelRegistration("inbound1", ChannelDirection.Inbound);
        ReadOnlySpan<ChannelRegistration> regs = [reg];

        Assert.True(client.TryRegisterChannels(regs, out var dict));
        Assert.Same(first, dict[reg]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_InboundCollidesWithExistingOutbound_ReturnsFalse()
    {
        var (client, server) = await StartedPairAsync();

        // Outbound binding on "x" — inbound registration for the same id must fail.
        client.OpenChannel("x");

        var reg = new ChannelRegistration("x", ChannelDirection.Inbound);
        ReadOnlySpan<ChannelRegistration> regs = [reg];

        Assert.False(client.TryRegisterChannels(regs, out var dict));
        Assert.Null(dict);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_NotStarted_ThrowsInvalidOperationException()
    {
        var (client, server) = CreatePair();

        var reg = new ChannelRegistration("ch", ChannelDirection.Outbound);
        ChannelRegistration[] regsArr = [reg];
        bool threw = false;
        try { _ = client.TryRegisterChannels(regsArr, out _); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_AfterGoAway_ThrowsInvalidOperationException()
    {
        var (client, server) = await StartedPairAsync();
        await client.GoAwayAsync();

        var reg = new ChannelRegistration("ch", ChannelDirection.Outbound);
        ChannelRegistration[] regsArr = [reg];
        bool threw = false;
        try { _ = client.TryRegisterChannels(regsArr, out _); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_OptionsChannelIdMismatch_ReturnsFalse()
    {
        var (client, server) = await StartedPairAsync();

        var bad = new ChannelRegistration("right", ChannelDirection.Outbound)
        {
            Options = new ChannelOptions { ChannelId = "wrong" },
        };
        ChannelRegistration[] regsArr = [bad];
        Assert.False(client.TryRegisterChannels(regsArr, out _));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_RollbackEmitsNoInitFrame_ServerNeverSeesChannel()
    {
        var (client, server) = await StartedPairAsync();

        // Pre-occupy "second" on client so the batch fails on the second registration.
        client.OpenChannel("second");

        var firstReg = new ChannelRegistration("first", ChannelDirection.Outbound);
        var secondReg = new ChannelRegistration("second", ChannelDirection.Outbound);
        ReadOnlySpan<ChannelRegistration> regs = [firstReg, secondReg];

        Assert.False(client.TryRegisterChannels(regs, out _));

        // Server must not see "first" — no INIT was sent for the rolled-back outbound.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.AcceptChannelAsync("first", cts.Token).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateChannelIdsInBatch_ReturnsFalse()
    {
        var (client, server) = await StartedPairAsync();

        var regs = new ChannelRegistration[] {
            new() { ChannelId = "dup", Direction = ChannelDirection.Outbound },
            new() { ChannelId = "dup", Direction = ChannelDirection.Outbound }
        };

        bool ok = client.TryRegisterChannels(regs, out _);
        Assert.False(ok);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
