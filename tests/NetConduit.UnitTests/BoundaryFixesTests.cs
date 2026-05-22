using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for bugs #20-#23 from the bug-hunter second-pass session.
/// Each test corresponds to an investigate/bug-NNN-*/ PoC and locks down the
/// fixed behavior so future refactors cannot silently reintroduce these bugs.
/// </summary>
public sealed class BoundaryFixesTests
{
    private sealed class StubChannelOwner : IChannelOwner
    {
        public int NotifyReadyCount;
        public List<(ushort Index, ulong Position)> SentAcks { get; } = [];

        public void NotifyReady(WriteChannel channel) => Interlocked.Increment(ref NotifyReadyCount);
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition)
        {
            SentAcks.Add((channelIndex, consumedPosition));
            return true;
        }
        public void NotifyEventHandlerException(Exception exception) { }
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
    }

    // --- Bug #20: oversized WriteAsync ----------------------------------

    [Fact]
    public async Task WriteAsync_PayloadExceedsSlab_ThrowsArgumentOutOfRange()
    {
        var owner = new StubChannelOwner();
        var channel = new WriteChannel(
            channelId: "oversize",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: 64 * 1024,
            sendTimeout: TimeSpan.FromSeconds(30),
            owner: owner);
        channel.MarkOpen();

        byte[] payload = new byte[128 * 1024];

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await channel.WriteAsync(payload));

        Assert.Equal("data", ex.ParamName);
        Assert.Contains("65528", ex.Message); // slabSize - FrameHeader.Size
        Assert.Contains("oversize", ex.Message);
    }

    [Fact]
    public async Task WriteAsync_PayloadAtMaxBudget_Succeeds()
    {
        var owner = new StubChannelOwner();
        const int slabSize = 64 * 1024;
        var channel = new WriteChannel(
            channelId: "exact",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        channel.MarkOpen();

        byte[] payload = new byte[slabSize - FrameHeader.Size];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(slabSize, ready.Length);
    }

    // --- Bug #21: sync Dispose with full slab ---------------------------

    [Fact]
    public async Task Dispose_WhenSlabIsFull_DoesNotThrow()
    {
        var owner = new StubChannelOwner();
        const int slabSize = 64 * 1024;
        var channel = new WriteChannel(
            channelId: "sync-dispose",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        channel.MarkOpen();

        // Fill the slab so the FIN frame (8 bytes) cannot fit.
        byte[] big = new byte[slabSize - FrameHeader.Size];
        await channel.WriteAsync(big);

        // Sync dispose must not throw even though WriteFinFrame cannot fit.
        var record = Record.Exception(() => channel.Dispose());
        Assert.Null(record);

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.LocalClose, channel.CloseReason);
    }

    // --- Bug #22: CloseAsync transitions to Closed ---------------------

    [Fact]
    public async Task CloseAsync_TransitionsToClosed_AndFiresClosedEvent()
    {
        var owner = new StubChannelOwner();
        var channel = new WriteChannel(
            channelId: "close-async",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: 64 * 1024,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        channel.MarkOpen();

        ChannelCloseReason? observedReason = null;
        channel.Closed += (_, e) => observedReason = e.Reason;

        await channel.CloseAsync();

        // CloseAsync queues a FIN and sets state to Closing. The Closing -> Closed
        // transition happens when the writer thread drains the FIN frame and
        // calls MarkSent (simulated here).
        Assert.Equal(ChannelState.Closing, channel.State);
        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size, ready.Length);
        channel.MarkSent(ready.Length);

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.LocalClose, channel.CloseReason);
        Assert.Equal(ChannelCloseReason.LocalClose, observedReason);
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        var owner = new StubChannelOwner();
        var channel = new WriteChannel(
            channelId: "close-idempotent",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: 64 * 1024,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        channel.MarkOpen();

        int closedFiredCount = 0;
        channel.Closed += (_, _) => Interlocked.Increment(ref closedFiredCount);

        await channel.CloseAsync();
        // Simulate writer-thread drain of the FIN frame to finalize the close.
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // Subsequent CloseAsync calls are no-ops.
        await channel.CloseAsync();
        await channel.CloseAsync();

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(1, closedFiredCount);
    }

    // --- Bug #23: peer ACK position is clamped -------------------------

    [Fact]
    public async Task OnAck_OutOfRangePositionFromPeer_IsClamped()
    {
        var owner = new StubChannelOwner();
        var channel = new WriteChannel(
            channelId: "malicious-ack",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: 1024 * 1024,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner,
            enableReplay: true);
        channel.MarkOpen();

        // Simulate a legitimate write + send so _sentPos is small but positive.
        await channel.WriteAsync(new byte[64]);
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // Peer sends an ACK position vastly beyond what we could possibly have sent.
        const long maliciousPosition = 2L * 1024L * 1024L;
        channel.OnAck(maliciousPosition);

        // The next write must still succeed — clamp must have prevented the slab
        // position fields from going negative.
        var record = await Record.ExceptionAsync(async () =>
            await channel.WriteAsync(new byte[16]));
        Assert.Null(record);
    }
}
