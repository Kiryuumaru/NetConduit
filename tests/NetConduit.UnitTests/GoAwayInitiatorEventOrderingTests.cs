using NetConduit.Enums;
using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in tests for the documented event ordering of graceful shutdown
/// on the *initiator* side (the multiplexer that called
/// <c>GoAwayAsync</c>). The events.md "Typical ordering → Graceful shutdown"
/// block must match the actual Phase-1 (Disconnected) / Phase-3 (SetClosed)
/// sequence inside <c>StreamMultiplexer.GoAwayAsync</c>. Issue #443.
/// </summary>
public sealed class GoAwayInitiatorEventOrderingTests
{
    [Fact]
    public async Task GoAwayAsync_FiresMuxDisconnected_BeforeChannelClosed_OnInitiator()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeCh = client.OpenChannel("ord");
        var readCh = await server.AcceptChannelAsync("ord", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);
        await readCh.WaitForReadyAsync(cts.Token);

        // Record observation order so we can assert the documented invariant:
        // mux.Disconnected(LocalDispose) fires BEFORE ch.Closed(MuxDisposed)
        // on the initiator side. The legacy events.md diagram showed the
        // opposite order; this test locks in the actual Phase-1/Phase-3 split
        // inside GoAwayAsync.
        var events = new List<string>();
        DisconnectReason? muxReason = null;
        ChannelCloseReason? chReason = null;

        var disconnectedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.Disconnected += (_, e) =>
        {
            lock (events) events.Add("mux.Disconnected");
            muxReason = e.Reason;
            disconnectedFired.TrySetResult();
        };
        writeCh.Closed += (_, e) =>
        {
            lock (events) events.Add("ch.Closed");
            chReason = e.Reason;
            closedFired.TrySetResult();
        };

        await client.GoAwayAsync(cts.Token);

        await Task.WhenAll(disconnectedFired.Task, closedFired.Task).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["mux.Disconnected", "ch.Closed"], events);
        Assert.Equal(DisconnectReason.LocalDispose, muxReason);
        Assert.Equal(ChannelCloseReason.MuxDisposed, chReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
    }
}
