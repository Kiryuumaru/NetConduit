using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression for #383 and #400 — the _disconnectedFired latch must be reset
/// on every (re)connect edge so that the next down-edge (transient drop or
/// terminal DisposeAsync) emits its own Disconnected event. Pre-fix, the
/// latch was set once on the first drop and never cleared, so any mux that
/// ever experienced a transient reconnect silently suppressed its terminal
/// Disconnected(LocalDispose) on dispose — the Connected/Disconnected event
/// pairing contract was broken by one missing event.
/// </summary>
public sealed class DisconnectedLatchResetAfterReconnectTests
{
    [Fact]
    public async Task DisposeAfterReconnect_FiresTerminalDisconnected()
    {
        // Broker with capacity 2: round 0 is killed mid-session, round 1
        // accepts the auto-reconnect. By the time we DisposeAsync, the mux
        // is connected again on the second cycle.
        var broker = new TransportBroker(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ClientFactory,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = broker.ServerFactory,
        });

        int connectedCount = 0;
        int disconnectedCount = 0;
        var transientDisconnect = new TaskCompletionSource();
        var reconnected = new TaskCompletionSource();

        client.Connected += (_, _) =>
        {
            if (Interlocked.Increment(ref connectedCount) == 2)
                reconnected.TrySetResult();
        };
        client.Disconnected += (_, _) =>
        {
            if (Interlocked.Increment(ref disconnectedCount) == 1)
                transientDisconnect.TrySetResult();
        };

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        // Force the first transport down-edge.
        broker.KillRound(0);
        await transientDisconnect.Task.WaitAsync(cts.Token);

        // Wait for the auto-reconnect cycle to complete.
        await reconnected.Task.WaitAsync(cts.Token);

        // Dispose while currently connected. The terminal Disconnected event
        // MUST fire even though _disconnectedFired was set by the transient
        // drop in the previous cycle.
        await client.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal(2, connectedCount);
        Assert.Equal(2, disconnectedCount);
    }

    [Fact]
    public async Task DisposeWithoutReconnect_FiresTerminalDisconnectedOnce()
    {
        // Invariant guard: a mux that has never experienced a drop must
        // still fire exactly one Disconnected on dispose. Verifies the reset
        // didn't break the simple Start→Dispose path.
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });

        int disconnectedCount = 0;
        client.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal(1, disconnectedCount);
    }
}
