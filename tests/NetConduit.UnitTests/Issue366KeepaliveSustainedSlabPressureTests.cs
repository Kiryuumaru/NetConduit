using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// #366 regression: under <em>sustained</em> control-slab pressure — the exact
/// signature of a half-open transport where the writer thread is itself
/// blocked on <c>transport.WriteStream.Write</c> and cannot drain coalescable
/// ACKs — <see cref="MuxKeepalive.RunAsync"/> must still tear the mux down
/// within the documented budget. The #355 fix correctly absorbed
/// <em>transient</em> slab pressure with <c>TryWriteRawFrame</c> + <c>continue</c>,
/// but the same code path silently disables liveness detection when the
/// pressure never clears: <c>missedPings</c> never increments and the mux
/// never throws <see cref="IOException"/>, defeating the half-open detection
/// contract.
/// </summary>
public sealed class Issue366KeepaliveSustainedSlabPressureTests
{
    [Fact]
    public async Task RunAsync_SustainedSlabPressure_ThrowsIOException_WithinMissedPingBudget()
    {
        var owner = new NoopOwner();
        var control = new WriteChannel(
            channelId: "ctrl",
            channelIndex: ChannelConstants.ControlChannel,
            priority: ChannelPriority.Highest,
            slabSize: FrameConstants.MinSlabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        control.MarkOpen();

        // Saturate the slab. The writer loop is never invoked, so the slab
        // stays full for the lifetime of the test — simulating a half-open
        // transport whose writer is permanently blocked.
        byte[] filler = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);
        int writes = (FrameConstants.MinSlabSize / filler.Length) + 16;
        for (int i = 0; i < writes; i++)
        {
            control.TryWriteRawFrame(filler);
        }

        byte[] pingProbe = ControlFrameBuilder.BuildControlFrame(FrameFlags.Ping, new byte[8]);
        Assert.False(control.TryWriteRawFrame(pingProbe),
            "Test precondition: control slab must remain saturated.");

        var conn = new MuxConnection
        {
            ControlChannel = control,
        };

        // pingInterval = 40ms, maxMissedPings = 3 → budget ≈ 120ms.
        // cts timeout = 2s is well beyond the budget; if the fix is reverted,
        // the loop will spin forever and the test will fail with
        // TaskCanceledException (assertion mismatch) rather than IOException.
        var keepalive = new MuxKeepalive(
            conn,
            pingInterval: TimeSpan.FromMilliseconds(40),
            pingTimeout: TimeSpan.FromMilliseconds(20),
            maxMissedPings: 3);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => keepalive.RunAsync(cts.Token));

        Assert.Contains("slab pressure", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(conn.PendingPong);
    }

    private sealed class NoopOwner : IChannelOwner
    {
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelOpened(string channelId) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
    }
}
