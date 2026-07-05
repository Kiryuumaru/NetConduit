using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression test for the FIN-on-Closed race from issue #546.
/// When DisposeAsync calls SetClosed before the writer loop drains the slab,
/// TryQueuePendingFinLocked rejected FIN queuing because state was Closed.
/// The fix allows FIN queuing in both Closing and Closed states.
///
/// The 64 KB cap described in #546 (256 × 256-byte messages arriving when
/// the reader isn't draining concurrently) is correct backpressure, not a
/// bug. With a concurrent reader, all messages arrive — the isolation tests
/// on the mesh branch confirm this. The actual bug is the shutdown-path
/// FIN race fixed here.
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
    /// (simulating DisposeAsync's post-close state transition when the slab
    /// was full), then drain via MarkSent. With the fix, TryQueuePendingFinLocked
    /// must allow queuing FIN in Closed state. Without the fix, FIN is never
    /// queued and the peer sees a truncated stream instead of a proper FIN.
    /// </summary>
    [Fact]
    public async Task MarkSent_AfterSetClosed_QueuesFinInClosedState()
    {
        var router = new TestRouter();
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

        for (int i = 0; i < 14; i++)
        {
            await channel.WriteAsync(new byte[1]);
            var ready = channel.TakeReady();
            channel.MarkSent(ready.Length);
        }

        await channel.WriteAsync(new byte[1]);
        var heldFrame = channel.TakeReady();
        await channel.CloseAsync();
        channel.SetClosed(ChannelCloseReason.LocalClose);
        channel.MarkSent(heldFrame.Length);

        var finFrame = channel.TakeReady();
        Assert.False(finFrame.IsEmpty,
            "Expected FIN frame to be queued by MarkSent after SetClosed.");
        Assert.Equal(FrameHeader.Size, finFrame.Length);
        var header = FrameHeader.Parse(finFrame.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);
    }
}
