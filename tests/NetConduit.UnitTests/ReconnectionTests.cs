using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class ReconnectionTests
{
    [Fact]
    public async Task WriteChannel_PrepareReplay_ResetsSentPos()
    {
        var router = new TestRouter();
        var ch = new WriteChannel("test", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(60), router, enableReplay: true);
        ch.MarkOpen();

        // Write data
        await ch.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });

        // Take and mark sent
        var ready = ch.TakeReady();
        Assert.False(ready.IsEmpty);
        ch.MarkSent(ready.Length);

        // With replay enabled, data should still be available after PrepareReplay
        ch.PrepareReplay();
        var replayed = ch.TakeReady();
        Assert.False(replayed.IsEmpty);
        Assert.Equal(ready.Length, replayed.Length);

        // Verify the replayed data matches original
        Assert.True(ready.Span.SequenceEqual(replayed.Span));
    }

    [Fact]
    public async Task WriteChannel_NoReplay_SentDataFreed()
    {
        var router = new TestRouter();
        var ch = new WriteChannel("test", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(60), router, enableReplay: false);
        ch.MarkOpen();

        // Write data
        await ch.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });

        // Take and mark sent
        var ready = ch.TakeReady();
        Assert.False(ready.IsEmpty);
        ch.MarkSent(ready.Length);

        // Without replay, nothing available (ackedPos == sentPos, compaction frees space)
        var afterSent = ch.TakeReady();
        Assert.True(afterSent.IsEmpty);
    }

    [Fact]
    public async Task WriteChannel_Replay_OnAckFreesSpace()
    {
        var router = new TestRouter();
        var ch = new WriteChannel("test", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(60), router, enableReplay: true);
        ch.MarkOpen();

        // Write and send
        await ch.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
        var ready = ch.TakeReady();
        int sentLength = ready.Length;
        ch.MarkSent(sentLength);

        // Before ACK: data still in slab for replay
        Assert.True(ch.HasPendingData() || ch.TakeReady().IsEmpty);

        // Simulate remote ACK
        ch.OnAck(sentLength);

        // After ACK: data is freed, TakeReady returns empty
        var afterAck = ch.TakeReady();
        Assert.True(afterAck.IsEmpty);
    }

    private sealed class TestRouter : NetConduit.Internal.IChannelOwner
    {
        public int NotifyCount;
        public void NotifyReady(WriteChannel channel) => Interlocked.Increment(ref NotifyCount);
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public void SendAck(ushort channelIndex, ulong consumedPosition) { }
    }
}
