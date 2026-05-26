using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for the <c>StreamMultiplexer</c> teardown event-emission
/// contracts that <c>IStreamMultiplexer</c> and <c>IChannel</c> document:
///
/// <list type="bullet">
///   <item>On peer-initiated <c>GoAway</c> the per-channel
///   <see cref="IChannel.Disconnected"/> reason on the receiving side must
///   be <see cref="DisconnectReason.GoAwayReceived"/>, matching the
///   mux-level <c>Disconnected</c> reason — not <c>TransportError</c>.
///   (Symmetric to the local-GoAway fix #391/#399.)</item>
///   <item>The mux-level <see cref="IStreamMultiplexer.ChannelClosed"/>
///   event is documented FIN-only and MUST NOT fire on
///   <see cref="ChannelCloseReason.LocalClose"/>,
///   <see cref="ChannelCloseReason.MuxDisposed"/>,
///   <see cref="ChannelCloseReason.TransportFailed"/>, or
///   <see cref="ChannelCloseReason.RemoteError"/>.</item>
/// </list>
/// </summary>
public sealed class TeardownEventReasonContractTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(2),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(2),
        });
        return (client, server);
    }

    private static async Task ReadyAsync(StreamMultiplexer a, StreamMultiplexer b)
    {
        a.Start();
        b.Start();
        await Task.WhenAll(
            a.WaitForReadyAsync().WaitAsync(TestTimeout),
            b.WaitForReadyAsync().WaitAsync(TestTimeout));
    }

    // =====================================================================
    // #429 — remote GoAway: per-channel Disconnected reason on the
    // RECEIVING side must match the mux-level reason (GoAwayReceived), not
    // TransportError.
    // =====================================================================

    [Fact]
    public async Task RemoteGoAway_FiresChannelDisconnectedWithGoAwayReceived()
    {
        var (client, server) = CreatePair();
        await ReadyAsync(client, server);

        var clientChannelReason = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientWrite = client.OpenChannel("ch");
        var serverRead = await server.AcceptChannelAsync("ch", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await Task.WhenAll(
            clientWrite.WaitForReadyAsync().WaitAsync(TestTimeout),
            serverRead.WaitForReadyAsync().WaitAsync(TestTimeout));

        clientWrite.Disconnected += (_, e) => clientChannelReason.TrySetResult(e.Reason);

        // Fire-and-forget so we don't block on server's drain (server has the
        // open read channel which keeps its own ActiveChannelCount > 0).
        _ = Task.Run(() => server.GoAwayAsync().AsTask());

        var observed = await clientChannelReason.Task.WaitAsync(TestTimeout);

        Assert.Equal(DisconnectReason.GoAwayReceived, observed);

        await clientWrite.DisposeAsync();
        await serverRead.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RemoteGoAway_ChannelAndMuxDisconnectedReasonsAgree()
    {
        var (client, server) = CreatePair();
        await ReadyAsync(client, server);

        var muxReason = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channelReason = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientWrite = client.OpenChannel("ch");
        var serverRead = await server.AcceptChannelAsync("ch", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await Task.WhenAll(
            clientWrite.WaitForReadyAsync().WaitAsync(TestTimeout),
            serverRead.WaitForReadyAsync().WaitAsync(TestTimeout));

        client.Disconnected += (_, e) => muxReason.TrySetResult(e.Reason);
        clientWrite.Disconnected += (_, e) => channelReason.TrySetResult(e.Reason);

        _ = Task.Run(() => server.GoAwayAsync().AsTask());

        var mux = await muxReason.Task.WaitAsync(TestTimeout);
        var ch = await channelReason.Task.WaitAsync(TestTimeout);

        Assert.Equal(DisconnectReason.GoAwayReceived, mux);
        Assert.Equal(mux, ch);

        await clientWrite.DisposeAsync();
        await serverRead.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // =====================================================================
    // #430 — mux-level ChannelClosed event is documented FIN-only. It MUST
    // NOT fire when the local consumer disposes a read channel before any
    // FIN arrives.
    // =====================================================================

    [Fact]
    public async Task ChannelClosedEvent_NotRaised_OnLocalReadChannelDispose()
    {
        var (client, server) = CreatePair();
        await ReadyAsync(client, server);

        var closedIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.ChannelClosed += (_, e) => closedIds.Add(e.ChannelId);

        var w = client.OpenChannel("local-dispose");
        var rc = await server.AcceptChannelAsync("local-dispose", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await Task.WhenAll(
            w.WaitForReadyAsync().WaitAsync(TestTimeout),
            rc.WaitForReadyAsync().WaitAsync(TestTimeout));

        // Server disposes its read channel BEFORE the writer FINs — this
        // path must not fire ChannelClosed per the documented FIN-only
        // contract.
        await rc.DisposeAsync();

        // Allow any spurious event to surface.
        await Task.Delay(100);

        Assert.DoesNotContain("local-dispose", closedIds);

        await w.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelClosedEvent_NullException_OnRemoteFin()
    {
        var (client, server) = CreatePair();
        await ReadyAsync(client, server);

        var closedArgs = new TaskCompletionSource<ChannelClosedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ChannelClosed += (_, e) =>
        {
            if (e.ChannelId == "fin-driven") closedArgs.TrySetResult(e);
        };

        var w = client.OpenChannel("fin-driven");
        var rc = await server.AcceptChannelAsync("fin-driven", CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await Task.WhenAll(
            w.WaitForReadyAsync().WaitAsync(TestTimeout),
            rc.WaitForReadyAsync().WaitAsync(TestTimeout));

        await w.CloseAsync().AsTask().WaitAsync(TestTimeout);

        // Drain so FIN is processed and the server fires ChannelClosed.
        var buf = new byte[64];
        while (await rc.ReadAsync(buf).AsTask().WaitAsync(TestTimeout) > 0) { }

        var args = await closedArgs.Task.WaitAsync(TestTimeout);

        // FIN-driven close carries no exception per the documented contract.
        Assert.Null(args.Exception);

        await rc.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
