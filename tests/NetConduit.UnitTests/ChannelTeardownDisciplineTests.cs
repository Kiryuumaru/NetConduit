using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regressions for the channel-teardown discipline contract:
/// every channel teardown path must (1) fire Disconnected before Closed for
/// channels that were ever connected, and (2) return the slab to ArrayPool
/// regardless of whether teardown was triggered by a duplicate-id collision,
/// a batch rollback, a mux dispose, a local GoAway, or a terminal first-connect
/// failure.
///
/// Covers issues #384 (RollbackPartialBatch slab leak), #385 (pre-handshake
/// channels stuck Opening on terminal first-connect failure), #390 (OpenChannel
/// slab leak on duplicate ChannelId), and #391 (channel Disconnected event
/// skipped on mux DisposeAsync / GoAwayAsync).
/// </summary>
public sealed class ChannelTeardownDisciplineTests
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

    // -----------------------------------------------------------------------
    // #391: Channel.Disconnected fires on mux DisposeAsync
    // -----------------------------------------------------------------------
    [Fact]
    public async Task DisposeAsync_FiresChannelDisconnectedBeforeClosed()
    {
        var (client, server) = await StartedPairAsync();

        var ch = client.OpenChannel(new ChannelOptions { ChannelId = "foo" });
        await ch.WaitForReadyAsync();
        Assert.True(ch.IsConnected);

        int disconnectedFired = 0;
        int closedFired = 0;
        int disconnectedBeforeClosed = 0;
        ch.Disconnected += (_, args) =>
        {
            Interlocked.Increment(ref disconnectedFired);
            if (Volatile.Read(ref closedFired) == 0)
                Interlocked.Increment(ref disconnectedBeforeClosed);
            Assert.Equal(DisconnectReason.LocalDispose, args.Reason);
        };
        ch.Closed += (_, _) => Interlocked.Increment(ref closedFired);

        await client.DisposeAsync();

        Assert.Equal(1, disconnectedFired);
        Assert.Equal(1, closedFired);
        Assert.Equal(1, disconnectedBeforeClosed);
        Assert.False(ch.IsConnected);

        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #391: Channel.Disconnected fires on mux GoAwayAsync
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GoAwayAsync_FiresChannelDisconnectedBeforeClosed()
    {
        var (client, server) = await StartedPairAsync();

        var ch = client.OpenChannel(new ChannelOptions { ChannelId = "foo" });
        await ch.WaitForReadyAsync();

        int disconnectedFired = 0;
        int closedFired = 0;
        ch.Disconnected += (_, args) =>
        {
            Interlocked.Increment(ref disconnectedFired);
            Assert.Equal(DisconnectReason.LocalDispose, args.Reason);
        };
        ch.Closed += (_, _) => Interlocked.Increment(ref closedFired);

        await client.GoAwayAsync();

        Assert.Equal(1, disconnectedFired);
        Assert.Equal(1, closedFired);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #385: Pre-handshake channels see ChannelClosedException via
    // WaitForReadyAsync when initial connect fails terminally, instead of
    // hanging forever in Opening state.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task TerminalFirstConnectFailure_FailsPreHandshakeChannelWaitForReady()
    {
        // Hold the connect attempt long enough for the test to open a channel
        // between Start() and the terminal failure. The factory waits on a TCS
        // that the test signals after OpenChannel completes; only then does it
        // throw, which sends MainLoopAsync into its terminal-failure catch.
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectException = new InvalidOperationException("unreachable peer");
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async _ =>
            {
                await releaseConnect.Task;
                throw connectException;
            },
            MaxAutoReconnectAttempts = 0,
        });

        mux.Start();

        // Open the channel while the connect attempt is parked, i.e. before
        // _isConnected ever becomes true. _readyTcs and the channel's slab are
        // both allocated; no MarkConnected / MarkOpen has run.
        var ch = mux.OpenChannel(new ChannelOptions { ChannelId = "test" });

        // Release the connect attempt — factory throws, MainLoopAsync enters
        // its terminal catch with hasConnectedBefore == false.
        releaseConnect.SetResult();

        // mux.WaitForReadyAsync observes the terminal failure (existing contract).
        var muxEx = await Assert.ThrowsAnyAsync<Exception>(() => mux.WaitForReadyAsync());
        Assert.NotNull(muxEx);

        // Pre-fix this hung forever; the channel's _readyTcs was never
        // completed because the hasConnectedBefore == false branch skipped
        // AbortAllChannels, leaving the pre-handshake channel in Opening
        // state with no terminal signal.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => ch.WaitForReadyAsync(cts.Token));
        Assert.False(cts.IsCancellationRequested, "ch.WaitForReadyAsync timed out instead of failing.");

        await mux.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #390: OpenChannel with duplicate ChannelId throws AND does not leave the
    // registry / index allocator in a broken state. The slab return is
    // structurally enforced via channel.Abort on the catch path; the
    // observable proof here is that subsequent OpenChannel calls succeed
    // and the registry remains consistent (i.e. the freshly-constructed
    // duplicate-id channel was not registered).
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OpenChannel_DuplicateId_ThrowsAndDoesNotPoisonRegistry()
    {
        var (client, server) = await StartedPairAsync();

        var ch1 = client.OpenChannel(new ChannelOptions { ChannelId = "foo" });
        Assert.NotNull(ch1);

        // Hammer the duplicate-id path repeatedly — each failed call pre-fix
        // leaked a slab and burned a channel index. Post-fix Abort returns
        // the slab; the index burn is intentional (monotonic allocator).
        for (int i = 0; i < 16; i++)
        {
            Assert.Throws<MultiplexerException>(
                () => client.OpenChannel(new ChannelOptions { ChannelId = "foo" }));
        }

        // Registry is still consistent: original channel is unchanged.
        Assert.Same(ch1, client.GetWriteChannel("foo"));

        // And a fresh OpenChannel with a different id still works.
        var ch2 = client.OpenChannel(new ChannelOptions { ChannelId = "bar" });
        await ch2.WaitForReadyAsync();
        Assert.True(ch2.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #384: TryRegisterChannels collision path rolls back ALL partially-
    // committed channels (slab returned, registry cleaned), so repeated
    // collisions do not corrupt subsequent operations.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task TryRegisterChannels_RepeatedCollision_RegistryStaysConsistent()
    {
        var (client, server) = await StartedPairAsync();

        // Pre-occupy the collision id.
        var preExisting = client.OpenChannel(new ChannelOptions { ChannelId = "taken" });
        Assert.NotNull(preExisting);

        // Repeat: 2 fresh outbound + 1 collision. Pre-fix this leaked 2 slabs
        // per iteration AND left the rolled-back channels in a non-Closed
        // state (no MarkDisconnected, no SetClosed).
        for (int i = 0; i < 16; i++)
        {
            ChannelRegistration[] regs =
            [
                new ChannelRegistration($"fresh-{i}-a", ChannelDirection.Outbound),
                new ChannelRegistration($"fresh-{i}-b", ChannelDirection.Outbound),
                new ChannelRegistration("taken", ChannelDirection.Outbound),
            ];

            Assert.False(client.TryRegisterChannels(regs, out var dict));
            Assert.Null(dict);

            // No partial commit visible.
            Assert.Null(client.GetWriteChannel($"fresh-{i}-a"));
            Assert.Null(client.GetWriteChannel($"fresh-{i}-b"));
        }

        // Subsequent batch with no collision still works.
        ChannelRegistration[] goodRegs =
        [
            new ChannelRegistration("good-a", ChannelDirection.Outbound),
            new ChannelRegistration("good-b", ChannelDirection.Outbound),
        ];
        Assert.True(client.TryRegisterChannels(goodRegs, out var goodDict));
        Assert.Equal(2, goodDict.Count);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
