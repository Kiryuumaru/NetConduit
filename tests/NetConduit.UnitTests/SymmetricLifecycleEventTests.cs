using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regressions for symmetric lifecycle-event emission:
/// every code path that triggers a lifecycle transition must fire the
/// corresponding event, not just the most-common path.
///
/// #378: Local GoAwayAsync must fire the mux-level Disconnected event,
/// symmetric with the remote-GoAway branch in MainLoopAsync that already
/// fires it. The transport is torn down identically on both paths.
///
/// #399: TryRegisterChannels must observe the publish-then-read invariant
/// for the transport-connected flag, symmetric with single-channel
/// OpenChannel. A captured snapshot from method entry would race with
/// MainLoopAsync setting _isConnected=true and running its MarkConnected
/// foreach, leaving freshly-committed channels IsConnected==false despite
/// a live transport.
/// </summary>
public sealed class SymmetricLifecycleEventTests
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
    // #378: Local GoAwayAsync fires mux-level Disconnected(LocalDispose)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GoAwayAsync_FiresMuxDisconnected()
    {
        var (client, server) = await StartedPairAsync();

        int disconnectedFired = 0;
        DisconnectReason? observedReason = null;
        client.Disconnected += (_, args) =>
        {
            Interlocked.Increment(ref disconnectedFired);
            observedReason = args.Reason;
        };

        await client.GoAwayAsync();

        Assert.Equal(1, disconnectedFired);
        Assert.Equal(DisconnectReason.LocalDispose, observedReason);

        // DisposeAsync after GoAwayAsync must NOT double-fire (gated by
        // _disconnectedFired which is now set on the GoAway path).
        await client.DisposeAsync();
        Assert.Equal(1, disconnectedFired);

        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #378: DisposeAsync alone (no prior GoAwayAsync) still fires Disconnected
    // exactly once — proves the GoAway-path fix doesn't break the existing
    // DisposeAsync fallback contract.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task DisposeAsync_WithoutGoAway_FiresMuxDisconnectedOnce()
    {
        var (client, server) = await StartedPairAsync();

        int disconnectedFired = 0;
        client.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedFired);

        await client.DisposeAsync();

        Assert.Equal(1, disconnectedFired);

        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // #399: TryRegisterChannels published outbound channels report
    // IsConnected==true and fire Connected after the mux is ready,
    // exercised under repeated handshake-window stress.
    //
    // The bug manifests when MainLoopAsync sets _isConnected=true and runs
    // its MarkConnected foreach AGAINST AN EMPTY REGISTRY SNAPSHOT between
    // the captured-snapshot read and Phase 2 commit. The window is narrow
    // on an in-memory duplex but repeated iterations exercise the seam.
    // Post-fix the contract is structural (publish-then-read on every
    // iteration) so the assertion holds deterministically.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task TryRegisterChannels_DuringInitialHandshake_FiresConnectedOnEveryChannel()
    {
        const int Iterations = 50;
        for (int iter = 0; iter < Iterations; iter++)
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

            client.Start();
            server.Start();

            // Race TryRegisterChannels against the in-flight handshake.
            // Dispatched on a separate task so the call's entry-time read
            // of _isConnected may legitimately observe false.
            var batchTask = Task.Run(() =>
            {
                ChannelRegistration[] regs =
                [
                    new ChannelRegistration($"out-a-{iter}", ChannelDirection.Outbound),
                    new ChannelRegistration($"out-b-{iter}", ChannelDirection.Outbound),
                ];
                bool ok = client.TryRegisterChannels(regs, out var dict);
                return (ok, dict);
            });

            await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
            var (ok, dict) = await batchTask;

            Assert.True(ok);
            Assert.NotNull(dict);

            // After the mux is ready, every committed outbound channel must
            // observe IsConnected==true. Pre-fix the captured-snapshot path
            // could leave them IsConnected==false on the first cycle.
            //
            // Allow a brief grace for the MainLoopAsync foreach to run on
            // channels that were already in the registry when foreach
            // executed (idempotent CAS in MarkConnected makes double-call
            // safe, but the foreach itself is the natural-path safety net).
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (dict.Values.All(c => c.IsConnected)) break;
                await Task.Delay(5);
            }

            foreach (var kvp in dict)
            {
                Assert.True(kvp.Value.IsConnected,
                    $"Iter {iter}: channel '{kvp.Key.ChannelId}' has IsConnected==false after handshake.");
            }

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
