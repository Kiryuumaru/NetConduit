using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for the burst-then-drain data loss scenario where
/// DisposeAsync transitions state to Closed before the FIN frame is queued.
/// After the fix, MarkSent must still queue FIN even in Closed state.
/// </summary>
public sealed class BurstDrainDataLossTests
{
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

    /// <summary>
    /// Fill the slab so FIN cannot fit when CloseAsync runs. Call SetClosed
    /// (simulating DisposeAsync's post-close state transition), then drain
    /// via MarkSent. With the fix, TryQueuePendingFinLocked must allow
    /// queuing FIN even in Closed state. Without the fix, FIN is never
    /// queued and the peer sees a truncated stream.
    /// </summary>
    [Fact]
    public async Task MarkSent_AfterSetClosed_QueuesFinInClosedState()
    {
        var router = new TestRouter();
        // slabSize = 135 = 15 frames × 9 bytes. After 14 frames drained via
        // MarkSent, the 15th frame fills the remaining 9 bytes exactly.
        // CloseAsync then has 0 bytes free so FIN (8 bytes) cannot fit.
        // SetClosed transitions state to Closed; subsequent MarkSent must
        // compact the slab and queue FIN.
        const int slabSize = 135;
        var channel = new WriteChannel(
            channelId: "test",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: router,
            enableReplay: true);
        channel.MarkOpen();

        // Write and drain 14 frames (126 bytes, 9 bytes free for the 15th).
        for (int i = 0; i < 14; i++)
        {
            await channel.WriteAsync(new byte[1]);
            var ready = channel.TakeReady();
            channel.MarkSent(ready.Length);
        }

        // Write the 15th frame — exactly fills the last 9 bytes (135 total).
        await channel.WriteAsync(new byte[1]);

        // TakeReady but don't MarkSent — simulates writer loop holding the slice.
        var heldFrame = channel.TakeReady();

        // CloseAsync: _finRequested=true. TryCompactLocked no-ops (_ackedPos=0
        // with replay). TryQueuePendingFinLocked: 0 bytes free < 8 → false.
        await channel.CloseAsync();

        // DisposeAsync path: SetClosed transitions to Closed.
        channel.SetClosed(ChannelCloseReason.LocalClose);

        // Writer loop completes its slice: MarkSent for the held frame.
        channel.MarkSent(heldFrame.Length);

        // MarkSent with _finRequested && enableReplay forces _ackedPos forward,
        // compaction clears the slab, and TryQueuePendingFinLocked queues FIN
        // (now accepted in Closed state). Drain the queued FIN via TakeReady.
        var finFrame = channel.TakeReady();
        Assert.False(finFrame.IsEmpty,
            "Expected FIN frame to be queued by MarkSent after SetClosed.");
        Assert.Equal(FrameHeader.Size, finFrame.Length);
        var header = FrameHeader.Parse(finFrame.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);
    }
}