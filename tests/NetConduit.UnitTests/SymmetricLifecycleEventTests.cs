using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Every code path that triggers a mux lifecycle transition must fire the
/// corresponding event. These tests pin the symmetry between paths so the
/// Disconnected and Connected contracts cannot regress on a secondary path
/// while continuing to work on the primary one.
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
    // Local GoAwayAsync fires mux-level Disconnected(LocalDispose).
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

        // DisposeAsync after GoAwayAsync must NOT double-fire: the
        // _disconnectedFired latch arbitrates terminal emission across
        // both teardown paths.
        await client.DisposeAsync();
        Assert.Equal(1, disconnectedFired);

        await server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // DisposeAsync without a prior GoAwayAsync still fires Disconnected
    // exactly once.
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
    // Outbound channels committed via TryRegisterChannels while the mux is
    // mid-handshake must observe IsConnected==true once the mux is ready.
    // Iterated to exercise the narrow window between transport-connected
    // and registry publication.
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

            // Race TryRegisterChannels against the in-flight handshake on a
            // separate task so the call may legitimately enter before the
            // mux's transport is connected.
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
            // observe IsConnected==true. Allow a brief grace for the
            // connect-path's MarkConnected walk to complete on channels
            // that were already in the registry when the walk ran.
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
