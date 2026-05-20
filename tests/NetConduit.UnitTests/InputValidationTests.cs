namespace NetConduit.UnitTests;

public sealed class InputValidationTests
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
    public void Create_NullOptions_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            StreamMultiplexer.Create(null!);
        });
    }

    [Fact]
    public void Create_NullStreamFactory_NotAllowedByCompiler()
    {
        // StreamFactory is 'required' - passing null is caught at compile-time
        // but if bypassed, Create still succeeds; Start will fail
        var mux = StreamMultiplexer.Create(new MultiplexerOptions { StreamFactory = null! });
        Assert.NotNull(mux);
    }

    [Fact]
    public async Task OpenChannel_NullId_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.ThrowsAny<ArgumentException>(() =>
        {
            client.OpenChannel((string)null!);
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_EmptyId_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentException>(() => client.OpenChannel(""));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_DuplicateId_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        client.OpenChannel("dupe");

        var ex = Assert.Throws<MultiplexerException>(() => client.OpenChannel("dupe"));
        Assert.Equal(ErrorCode.ChannelExists, ex.ErrorCode);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_NullId_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await server.AcceptChannelAsync(null!, cts.Token);
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_EmptyId_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentException>(() => server.AcceptChannel(""));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_VeryLongId_HandledCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // A reasonably long channel ID (not absurdly long)
        string longId = new string('x', 200);
        var ch = client.OpenChannel(longId);
        var readCh = await server.AcceptChannelAsync(longId, cts.Token);

        Assert.Equal(longId, ch.ChannelId);
        Assert.Equal(longId, readCh.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_MaxUtf8Id_IsAccepted()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        string maxId = new('x', 1024);
        var ch = client.OpenChannel(maxId);
        var readCh = await server.AcceptChannelAsync(maxId, cts.Token);

        Assert.Equal(maxId, ch.ChannelId);
        Assert.Equal(maxId, readCh.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_OverMaxUtf8Id_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        string overMaxId = new('x', 1025);

        Assert.Throws<ArgumentException>(() => client.OpenChannel(overMaxId));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_OverMaxUnicodeUtf8Id_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        string overMaxId = string.Concat(Enumerable.Repeat("🚀", 257));

        Assert.Throws<ArgumentException>(() => client.OpenChannel(overMaxId));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_InvalidId_DoesNotRegisterOrIncrementStats()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        string overMaxId = new('x', 1025);

        Assert.Throws<ArgumentException>(() => client.OpenChannel(overMaxId));

        Assert.Empty(client.ActiveChannelIds);
        Assert.Equal(0, client.ActiveChannelCount);
        Assert.Equal(0, client.Stats.OpenChannels);
        Assert.Equal(0, client.Stats.TotalChannelsOpened);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_OverMaxUtf8Id_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        string overMaxId = new('x', 1025);

        Assert.Throws<ArgumentException>(() => server.AcceptChannel(overMaxId));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SpecialCharacters_HandledCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        string specialId = "test/channel#1 (special)";
        var ch = client.OpenChannel(specialId);
        var readCh = await server.AcceptChannelAsync(specialId, cts.Token);

        Assert.Equal(specialId, ch.ChannelId);
        Assert.Equal(specialId, readCh.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_UnicodeId_HandledCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        string unicodeId = "通道/テスト/🚀";
        var ch = client.OpenChannel(unicodeId);
        var readCh = await server.AcceptChannelAsync(unicodeId, cts.Token);

        Assert.Equal(unicodeId, ch.ChannelId);
        Assert.Equal(unicodeId, readCh.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_NullOptions_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.ThrowsAny<Exception>(() =>
        {
            client.OpenChannel((ChannelOptions)null!);
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_CancelledToken_SmallWriteSucceedsWhenSlabHasSpace()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var ch = client.OpenChannel("cancel-write");
        await server.AcceptChannelAsync("cancel-write", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Small write with cancelled token succeeds because slab has space
        // (token is only checked when waiting for backpressure release)
        await ch.WriteAsync(new byte[] { 1 }, cts.Token);

        // Verify the data was actually buffered and can be read
        await ch.DisposeAsync();

        using var validCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var readCh = server.GetReadChannel("cancel-write")
            ?? throw new InvalidOperationException("Channel not found");
        var buf = new byte[1];
        int read = await readCh.ReadAsync(buf, validCts.Token);
        Assert.Equal(1, read);
        Assert.Equal(1, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_CancelledToken_ThrowsOperationCanceled()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        client.OpenChannel("cancel-read");
        var readCh = await server.AcceptChannelAsync("cancel-read", cts1.Token);

        using var cts2 = new CancellationTokenSource();
        cts2.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await readCh.ReadAsync(new byte[16], cts2.Token);
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StartCalledTwice_ThrowsInvalidOperation()
    {
        var (client, server) = CreatePair();
        client.Start();

        Assert.ThrowsAny<InvalidOperationException>(() => client.Start());

        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // ---- SlabSize bound validation (arch-010) ----
    //
    // docs/api/channel-options.md states: "SlabSize must be between 64 KiB and 64 MiB."
    // These tests pin that contract: the public constructor / factory rejects out-of-range
    // values at the boundary instead of producing confusing downstream failures.

    private const int MinSlabSize = 64 * 1024;
    private const int MaxSlabSize = 64 * 1024 * 1024;

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_BelowMin_Throws()
    {
        var duplex = new DuplexMemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
                DefaultChannelOptions = new DefaultChannelOptions { SlabSize = MinSlabSize - 1 },
            }));
    }

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_AboveMax_Throws()
    {
        var duplex = new DuplexMemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
                DefaultChannelOptions = new DefaultChannelOptions { SlabSize = MaxSlabSize + 1 },
            }));
    }

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_Zero_Throws()
    {
        var duplex = new DuplexMemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
                DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 0 },
            }));
    }

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_Negative_Throws()
    {
        var duplex = new DuplexMemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
                DefaultChannelOptions = new DefaultChannelOptions { SlabSize = -1 },
            }));
    }

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_ExactlyMin_Succeeds()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = MinSlabSize },
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public void Create_DefaultChannelOptionsSlabSize_ExactlyMax_Succeeds()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = MaxSlabSize },
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public async Task OpenChannel_SlabSize_BelowMin_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = MinSlabSize - 1 }));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SlabSize_AboveMax_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = MaxSlabSize + 1 }));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SlabSize_Zero_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = 0 }));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SlabSize_Negative_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = -1 }));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SlabSize_ExactlyMin_Succeeds()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = MinSlabSize });
        Assert.NotNull(channel);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_SlabSize_ExactlyMax_Succeeds()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel(new ChannelOptions { ChannelId = "x", SlabSize = MaxSlabSize });
        Assert.NotNull(channel);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // ---- Timing / multiplier option validation (arch-012) ----
    //
    // docs/api/multiplexer-options.md Validation section: see "Timing options".
    // Escape hatches preserved: PingInterval = Zero disables keepalive;
    // ConnectionTimeout = InfiniteTimeSpan disables per-attempt timeout.

    private static StreamFactoryDelegate AnyFactory() =>
        _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA);

    [Fact]
    public void Create_NegativePingInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                PingInterval = TimeSpan.FromSeconds(-1),
            }));
    }

    [Fact]
    public void Create_ZeroPingInterval_DisablesKeepalive_Succeeds()
    {
        // Escape hatch: PingInterval = TimeSpan.Zero disables keepalive entirely.
        // PingTimeout / MaxMissedPings are not validated in this mode.
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = AnyFactory(),
            PingInterval = TimeSpan.Zero,
            PingTimeout = TimeSpan.Zero,
            MaxMissedPings = 0,
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public void Create_ZeroPingTimeoutWithKeepaliveEnabled_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                PingInterval = TimeSpan.FromSeconds(30),
                PingTimeout = TimeSpan.Zero,
            }));
    }

    [Fact]
    public void Create_NegativePingTimeoutWithKeepaliveEnabled_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                PingInterval = TimeSpan.FromSeconds(30),
                PingTimeout = TimeSpan.FromSeconds(-1),
            }));
    }

    [Fact]
    public void Create_ZeroMaxMissedPingsWithKeepaliveEnabled_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                PingInterval = TimeSpan.FromSeconds(30),
                MaxMissedPings = 0,
            }));
    }

    [Fact]
    public void Create_NegativeMaxMissedPingsWithKeepaliveEnabled_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                PingInterval = TimeSpan.FromSeconds(30),
                MaxMissedPings = -1,
            }));
    }

    [Fact]
    public void Create_NegativeGoAwayTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                GoAwayTimeout = TimeSpan.FromSeconds(-1),
            }));
    }

    [Fact]
    public void Create_ZeroGoAwayTimeout_Succeeds()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = AnyFactory(),
            GoAwayTimeout = TimeSpan.Zero,
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public void Create_NegativeAutoReconnectDelay_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectDelay = TimeSpan.FromMilliseconds(-1),
            }));
    }

    [Fact]
    public void Create_MaxAutoReconnectDelayBelowBase_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectDelay = TimeSpan.FromSeconds(30),
                MaxAutoReconnectDelay = TimeSpan.FromSeconds(5),
            }));
    }

    [Fact]
    public void Create_BackoffMultiplierBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectBackoffMultiplier = 0.5,
            }));
    }

    [Fact]
    public void Create_ZeroBackoffMultiplier_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectBackoffMultiplier = 0.0,
            }));
    }

    [Fact]
    public void Create_NegativeBackoffMultiplier_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectBackoffMultiplier = -2.0,
            }));
    }

    [Fact]
    public void Create_NaNBackoffMultiplier_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                AutoReconnectBackoffMultiplier = double.NaN,
            }));
    }

    [Fact]
    public void Create_BackoffMultiplierExactlyOne_Succeeds()
    {
        // 1.0 means "no growth" - fixed-interval retry. Permitted.
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = AnyFactory(),
            AutoReconnectBackoffMultiplier = 1.0,
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public void Create_NegativeConnectionTimeoutThatIsNotInfinite_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = AnyFactory(),
                ConnectionTimeout = TimeSpan.FromMilliseconds(-100),
            }));
    }

    [Fact]
    public void Create_InfiniteConnectionTimeout_Succeeds()
    {
        // Escape hatch: Timeout.InfiniteTimeSpan disables per-attempt timeout.
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = AnyFactory(),
            ConnectionTimeout = Timeout.InfiniteTimeSpan,
        });
        Assert.NotNull(mux);
    }

    [Fact]
    public void Create_ZeroConnectionTimeout_Succeeds()
    {
        // Zero also disables per-attempt timeout enforcement (matches the existing
        // "> Zero && != Infinite" runtime gate).
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = AnyFactory(),
            ConnectionTimeout = TimeSpan.Zero,
        });
        Assert.NotNull(mux);
    }
}
