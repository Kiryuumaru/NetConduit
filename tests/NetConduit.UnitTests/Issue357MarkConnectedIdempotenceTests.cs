using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression test for issue #357.
///
/// <see cref="WriteChannel.MarkConnected"/> and
/// <see cref="ReadChannel.MarkConnected"/> were non-idempotent: every call
/// raised the <c>Connected</c> event. The mux invokes <c>MarkConnected</c>
/// from two distinct sites during a single reconnect transition (the per-
/// channel foreach in <c>MainLoopAsync</c> after handshake, and the fast
/// path in <c>OpenChannel</c>/<c>AcceptChannel</c> when <c>_isConnected</c>
/// is already true), so a channel registered between those two sites
/// received two Connected events for one transport-up transition.
///
/// The fix is a CAS-guarded <c>_connectedFired</c> flag that promotes 0->1
/// exactly once per connect transition and is reset by
/// <c>MarkDisconnected</c> so the next reconnect can re-fire.
/// </summary>
public sealed class Issue357MarkConnectedIdempotenceTests
{
    [Fact]
    public void WriteChannel_MarkConnected_FiresOnceAcrossDuplicateCalls()
    {
        var router = new TestRouter();
        var ch = new WriteChannel("test", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(60), router, enableReplay: false);

        int connectedCount = 0;
        ch.Connected += (_, _) => Interlocked.Increment(ref connectedCount);

        // Two concurrent MarkConnected calls model the OpenChannel / MainLoop race.
        ch.MarkConnected();
        ch.MarkConnected();

        Assert.Equal(1, connectedCount);
        Assert.True(ch.IsConnected);
    }

    [Fact]
    public void ReadChannel_MarkConnected_FiresOnceAcrossDuplicateCalls()
    {
        var router = new TestRouter();
        var ch = new ReadChannel("test", 1, ChannelPriority.Normal, 64 * 1024, router);

        int connectedCount = 0;
        ch.Connected += (_, _) => Interlocked.Increment(ref connectedCount);

        ch.MarkConnected();
        ch.MarkConnected();

        Assert.Equal(1, connectedCount);
        Assert.True(ch.IsConnected);
    }

    [Fact]
    public void WriteChannel_MarkConnected_RefiresAfterDisconnect()
    {
        var router = new TestRouter();
        var ch = new WriteChannel("test", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(60), router, enableReplay: true);

        int connectedCount = 0;
        int disconnectedCount = 0;
        ch.Connected += (_, _) => Interlocked.Increment(ref connectedCount);
        ch.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        // First connect cycle
        ch.MarkConnected();
        ch.MarkConnected(); // duplicate — must be ignored
        Assert.Equal(1, connectedCount);
        Assert.Equal(0, disconnectedCount);

        // Disconnect resets the latch
        ch.MarkDisconnected(DisconnectReason.TransportError);
        ch.MarkDisconnected(DisconnectReason.TransportError); // duplicate — must be ignored
        Assert.Equal(1, connectedCount);
        Assert.Equal(1, disconnectedCount);
        Assert.False(ch.IsConnected);

        // Second connect cycle re-fires
        ch.MarkConnected();
        ch.MarkConnected(); // duplicate — must be ignored
        Assert.Equal(2, connectedCount);
        Assert.Equal(1, disconnectedCount);
        Assert.True(ch.IsConnected);
    }

    [Fact]
    public void ReadChannel_MarkConnected_RefiresAfterDisconnect()
    {
        var router = new TestRouter();
        var ch = new ReadChannel("test", 1, ChannelPriority.Normal, 64 * 1024, router);

        int connectedCount = 0;
        int disconnectedCount = 0;
        ch.Connected += (_, _) => Interlocked.Increment(ref connectedCount);
        ch.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        ch.MarkConnected();
        ch.MarkConnected();
        Assert.Equal(1, connectedCount);

        ch.MarkDisconnected(DisconnectReason.TransportError);
        ch.MarkDisconnected(DisconnectReason.TransportError);
        Assert.Equal(1, disconnectedCount);
        Assert.False(ch.IsConnected);

        ch.MarkConnected();
        ch.MarkConnected();
        Assert.Equal(2, connectedCount);
        Assert.Equal(1, disconnectedCount);
        Assert.True(ch.IsConnected);
    }

    [Fact]
    public async Task OpenChannelDuringReconnect_FiresConnectedExactlyOnce()
    {
        // Drive the actual race described in #357: open a channel concurrently
        // with the MainLoopAsync post-handshake foreach. Without idempotence,
        // a fraction of runs see the channel's Connected event fire twice.
        // With the fix every channel always fires Connected exactly once per
        // connect transition.
        var factory = new ReconnectableTransportFactory();
        try
        {
            var client = StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = ct => factory.CreateSideA(ct),
                PingInterval = TimeSpan.Zero,
                MaxAutoReconnectAttempts = 5,
                AutoReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            var server = StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = ct => factory.CreateSideB(ct),
                PingInterval = TimeSpan.Zero,
                MaxAutoReconnectAttempts = 5,
                AutoReconnectDelay = TimeSpan.FromMilliseconds(1),
            });

            client.Start();
            server.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.WhenAll(
                client.WaitForReadyAsync(cts.Token),
                server.WaitForReadyAsync(cts.Token));

            // Drive many reconnect cycles, opening a fresh channel during each
            // reconnect to maximise the chance of interleaving the OpenChannel
            // MarkConnected fast path with the MainLoopAsync foreach.
            const int Cycles = 30;
            var perChannelCounts = new System.Collections.Concurrent.ConcurrentBag<(string id, int count)>();

            for (int i = 0; i < Cycles; i++)
            {
                string id = $"race-{i}";
                int connectedCount = 0;
                var clientReconnected = new TaskCompletionSource();
                EventHandler handler = (_, _) => clientReconnected.TrySetResult();
                client.Connected += handler;

                factory.KillCurrentTransport();
                await clientReconnected.Task.WaitAsync(cts.Token);
                client.Connected -= handler;

                // Race window: MainLoopAsync is currently between
                // _isConnected = true and the per-channel foreach. Opening a
                // channel now triggers the OpenChannel fast-path MarkConnected.
                var ch = client.OpenChannel(id);
                ch.Connected += (_, _) => Interlocked.Increment(ref connectedCount);

                // Give MainLoopAsync time to complete the foreach (which would
                // otherwise call MarkConnected a second time on this channel).
                await Task.Delay(20, cts.Token);

                perChannelCounts.Add((id, Volatile.Read(ref connectedCount)));
            }

            foreach (var (id, count) in perChannelCounts)
            {
                Assert.True(
                    count <= 1,
                    $"Channel '{id}' fired Connected {count} times in one transport-up transition (#357).");
            }

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private sealed class TestRouter : IChannelOwner
    {
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
    }
}
