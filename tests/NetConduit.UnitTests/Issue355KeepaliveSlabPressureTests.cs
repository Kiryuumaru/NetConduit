using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// #355 regression: <see cref="MuxKeepalive"/> must use the non-throwing
/// <c>TryWriteRawFrame</c> for the PING path. Under sustained control-slab
/// pressure (parallel of #291/#336), an outgoing PING that hits a full slab
/// MUST NOT fault the keepalive loop with <see cref="InvalidOperationException"/>
/// — that propagates out of <c>RunAsync</c>, faults <c>KeepaliveTask</c>, and
/// tears the entire mux down on a healthy wire.
/// </summary>
public sealed class Issue355KeepaliveSlabPressureTests
{
    [Fact]
    public async Task RunAsync_ControlSlabFull_DoesNotThrow_AndKeepsPendingPongClear()
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

        // Saturate the control slab. The writer loop is never invoked here, so
        // _sentPos stays at zero, compaction cannot help, and TryWriteRawFrame
        // will keep returning false once the slab fills.
        byte[] filler = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);
        int writes = (FrameConstants.MinSlabSize / filler.Length) + 16;
        for (int i = 0; i < writes; i++)
        {
            control.TryWriteRawFrame(filler);
        }

        // A 16-byte ping frame (8B header + 8B token payload) MUST NOT fit any
        // longer — this is the precondition of the bug.
        byte[] pingProbe = ControlFrameBuilder.BuildControlFrame(FrameFlags.Ping, new byte[8]);
        Assert.False(control.TryWriteRawFrame(pingProbe),
            "Test precondition: control slab must be fully saturated.");

        var conn = new MuxConnection
        {
            ControlChannel = control,
        };

        var keepalive = new MuxKeepalive(
            conn,
            pingInterval: TimeSpan.FromMilliseconds(40),
            pingTimeout: TimeSpan.FromMilliseconds(20),
            maxMissedPings: 5);

        // Cancel inside the slab-pressure budget (5 * 40ms = 200ms). #355's
        // contract is "transient pressure must NOT fault the loop" — that is,
        // a small number of cycles under pressure must absorb cleanly. The
        // companion #366 test verifies that sustained pressure DOES tear down
        // once the budget is exceeded.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        // Without the fix: the first SendPing call throws InvalidOperationException
        // ("Slab full for raw frame.") out of RunAsync — the task faults.
        // With the fix: TrySendPing returns false, the just-installed PendingPong
        // is CompareExchange-cleared, the loop continues, and the cancellation
        // cleanly exits via OperationCanceledException → suppressed → completion.
        await keepalive.RunAsync(cts.Token);

        // PendingPong must be null after the cancelled-but-slab-pressured run:
        // every failed send path clears the just-installed pending so the TCS
        // does not dangle.
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
