using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Coverage for <see cref="StreamMultiplexer.HandleInitFrame"/>'s
/// state-guard discipline: the inbound channel-creation path must respect
/// the same invariants the local <c>OpenChannel</c> / <c>AcceptChannel</c>
/// paths enforce. Specifically it must (a) refuse to register a brand-new
/// inbound channel after the local side has latched <c>_isShuttingDown</c>
/// via <c>GoAwayAsync</c>, and (b) never adopt a pending-accept channel
/// whose consumer has already disposed it.
/// </summary>
public sealed class HandleInitFrameStateGuardTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(TimeSpan? goAwayTimeout = null)
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = goAwayTimeout ?? TimeSpan.FromSeconds(30),
        });
        return (client, server);
    }

    // =====================================================================
    // After local GoAwayAsync latches _isShuttingDown, a peer-sent INIT for
    // a brand-new channel id must NOT register a new inbound channel:
    //   - ChannelAccepted must not fire for the late id
    //   - TotalChannelsOpened must not increment
    //   - drain must not be extended indefinitely by the ghost channel
    // (A pre-existing AcceptChannel("late") would still be honoured; the
    // guard only blocks brand-new inbound channels.)
    // =====================================================================

    [Fact]
    public async Task HandleInitFrame_DoesNotRegisterNewInboundChannel_AfterGoAwayShutdown()
    {
        var (client, server) = CreatePair(goAwayTimeout: TimeSpan.FromSeconds(3));
        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        // Pre-open one channel so server's drain loop blocks (ActiveChannelCount > 0).
        var keepaliveWriter = client.OpenChannel("keepalive");
        var keepaliveReader = await server.AcceptChannelAsync("keepalive", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await keepaliveReader.WaitForReadyAsync().WaitAsync(TestTimeout);

        int totalOpenedBefore = server.Stats.TotalChannelsOpened;

        var acceptedIds = new List<string>();
        server.ChannelAccepted += (_, args) =>
        {
            lock (acceptedIds) acceptedIds.Add(args.ChannelId);
        };

        // Start the local GoAwayAsync. It synchronously stages the GoAway frame
        // and latches _isShuttingDown before entering the drain wait. The drain
        // wait will block because `keepalive` keeps ActiveChannelCount > 0.
        var goAwayTask = server.GoAwayAsync();

        // Wait until _isShuttingDown is observed by other threads.
        var spinDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!server.IsShuttingDown && DateTime.UtcNow < spinDeadline)
            await Task.Delay(5);
        Assert.True(server.IsShuttingDown, "server.IsShuttingDown should latch synchronously inside GoAwayAsync");

        // Peer opens a brand-new channel AFTER the latch. From the peer's view
        // the wire was up and GoAway had not arrived; INIT goes out normally.
        var ghostWriter = client.OpenChannel("ghost-after-goaway");

        // Give the server reader thread time to dispatch the INIT.
        await Task.Delay(500);

        // Close the keepalive so drain can finish and we can shut down.
        await keepaliveWriter.DisposeAsync();
        await keepaliveReader.DisposeAsync();

        await goAwayTask.AsTask().WaitAsync(TestTimeout);

        lock (acceptedIds)
        {
            Assert.DoesNotContain("ghost-after-goaway", acceptedIds);
        }

        // Counter must not include the rejected ghost channel. It is fine for
        // it to have advanced for keepalive (which was opened pre-goaway), so
        // assert against the pre-goaway baseline + 0 ghosts.
        Assert.Equal(totalOpenedBefore, server.Stats.TotalChannelsOpened);

        await ghostWriter.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // Pre-existing AcceptChannel("late") issued BEFORE GoAwayAsync must
    // still be honoured by the inbound INIT after GoAwayAsync — the guard
    // is narrow ("no NEW inbound channels"), not "drop all inbound INITs".
    // =====================================================================

    [Fact]
    public async Task HandleInitFrame_HonoursPendingAccept_RegisteredBeforeGoAwayShutdown()
    {
        var (client, server) = CreatePair(goAwayTimeout: TimeSpan.FromSeconds(3));
        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        var keepaliveWriter = client.OpenChannel("keepalive");
        var keepaliveReader = await server.AcceptChannelAsync("keepalive", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await keepaliveReader.WaitForReadyAsync().WaitAsync(TestTimeout);

        // Pre-register the accept BEFORE calling GoAwayAsync.
        var pendingReader = server.AcceptChannel("preregistered");

        var goAwayTask = server.GoAwayAsync();

        var spinDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!server.IsShuttingDown && DateTime.UtcNow < spinDeadline)
            await Task.Delay(5);

        // Peer opens the pre-registered id after the latch — must still wire up.
        var preregisteredWriter = client.OpenChannel("preregistered");

        await pendingReader.WaitForReadyAsync().WaitAsync(TestTimeout);
        Assert.Equal(ChannelState.Open, pendingReader.State);

        await preregisteredWriter.DisposeAsync();
        await pendingReader.DisposeAsync();
        await keepaliveWriter.DisposeAsync();
        await keepaliveReader.DisposeAsync();
        await goAwayTask.AsTask().WaitAsync(TestTimeout);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // TOCTOU lock-in: under a stress loop where the consumer disposes a
    // pending-accept channel while the peer's INIT for the same id is in
    // flight, the registry must never end up holding the disposed instance.
    // The fresh channel that gets created in its place must be a brand-new
    // ReadChannel — i.e. !ReferenceEquals(disposedPending, registered).
    // =====================================================================

    [Fact]
    public async Task HandleInitFrame_RaceWithPendingAcceptDispose_NeverAdoptsDisposedInstance()
    {
        const int iterations = 200;
        await using var serverChannelsObserved = new ConcurrentChannelBag();

        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        using var acceptCts = new CancellationTokenSource(TestTimeout);
        var acceptPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in server.AcceptChannelsAsync(ct: acceptCts.Token))
                {
                    serverChannelsObserved.Add(ch.ChannelId, ch);
                }
            }
            catch (OperationCanceledException) { /* expected on test end */ }
        });

        for (int i = 0; i < iterations; i++)
        {
            string id = $"race-{i}";

            // Pre-register accept on server.
            var pending = (ReadChannel)server.AcceptChannel(id);

            // Race: peer opens at roughly the same moment we dispose the pending.
            var openTask = Task.Run(() =>
            {
                var w = client.OpenChannel(id);
                // Best-effort flush of an INIT-only handshake; no payload needed.
                return w;
            });

            await pending.DisposeAsync();

            var writer = await openTask;

            // Drain the peer side so we don't accumulate orphan write channels.
            await writer.DisposeAsync();

            // The disposed pending instance MUST stay Closed. If HandleInitFrame
            // adopted it post-Dispose, side effects fired but State is whatever
            // SetClosed left — still Closed (the State guard catches that case),
            // however the registry would hold this exact instance under its
            // channel index. The structural invariant: any ReadChannel observed
            // through AcceptChannelsAsync for this id is a fresh instance.
            Assert.Equal(ChannelState.Closed, pending.State);

            if (serverChannelsObserved.TryGet(id, out var arrived))
            {
                Assert.NotSame(pending, arrived);
                Assert.Equal(ChannelState.Open, arrived.State);
            }
            // If `arrived` is null, peer-INIT lost the race entirely (server
            // saw the pending Dispose first → fresh channel never made it to
            // AcceptChannelsAsync because peer closed its writer immediately).
            // That branch is also fine: nothing was adopted, nothing leaked.
        }

        acceptCts.Cancel();
        try { await acceptPump; } catch (OperationCanceledException) { }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private sealed class ConcurrentChannelBag : IAsyncDisposable
    {
        private readonly Dictionary<string, IReadChannel> _byId = new();
        private readonly object _lock = new();

        public void Add(string id, IReadChannel channel)
        {
            lock (_lock) _byId[id] = channel;
        }

        public bool TryGet(string id, out IReadChannel channel)
        {
            lock (_lock) return _byId.TryGetValue(id, out channel!);
        }

        public async ValueTask DisposeAsync()
        {
            List<IReadChannel> snapshot;
            lock (_lock) snapshot = _byId.Values.ToList();
            foreach (var ch in snapshot)
            {
                try { await ch.DisposeAsync(); } catch { /* ignore */ }
            }
        }
    }
}
