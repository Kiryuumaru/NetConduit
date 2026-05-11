using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class WriteChannelTests
{
    private sealed class TestRouter : IChannelOwner
    {
        public int NotifyCount;
        public WriteChannel? LastNotified;

        public void NotifyReady(WriteChannel channel)
        {
            LastNotified = channel;
            Interlocked.Increment(ref NotifyCount);
        }

        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
    }

    private static WriteChannel CreateChannel(TestRouter? router = null, int slabSize = 64 * 1024)
    {
        router ??= new TestRouter();
        var channel = new WriteChannel(
            channelId: "test",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(60),
            owner: router);
        channel.MarkOpen();
        return channel;
    }

    [Fact]
    public async Task WriteAsync_BuildsFrameInSlab()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);
        byte[] payload = [1, 2, 3, 4, 5];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size + 5, ready.Length);

        // Parse the header
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(1, header.ChannelIndex);
        Assert.Equal(FrameFlags.Data, header.Flags);
        Assert.Equal(5, header.PayloadLength);

        // Verify payload
        Assert.True(ready.Span.Slice(FrameHeader.Size, 5).SequenceEqual(payload));
    }

    [Fact]
    public async Task WriteAsync_NotifiesRouter()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);

        await channel.WriteAsync(new byte[10]);

        Assert.Equal(1, router.NotifyCount);
        Assert.Same(channel, router.LastNotified);
    }

    [Fact]
    public async Task WriteAsync_MultipleWrites_AccumulateInSlab()
    {
        var channel = CreateChannel();

        await channel.WriteAsync(new byte[100]);
        await channel.WriteAsync(new byte[200]);

        var ready = channel.TakeReady();
        int expectedSize = (FrameHeader.Size + 100) + (FrameHeader.Size + 200);
        Assert.Equal(expectedSize, ready.Length);
    }

    [Fact]
    public void TakeReady_ReturnsEmpty_WhenNoData()
    {
        var channel = CreateChannel();
        var ready = channel.TakeReady();
        Assert.True(ready.IsEmpty);
    }

    [Fact]
    public async Task MarkSent_AdvancesSentPosition()
    {
        var channel = CreateChannel();
        await channel.WriteAsync(new byte[10]);

        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        var readyAgain = channel.TakeReady();
        Assert.True(readyAgain.IsEmpty);
    }

    [Fact]
    public async Task WriteAsync_ThrowsOnClosedChannel()
    {
        var channel = CreateChannel();
        channel.SetClosed(ChannelCloseReason.LocalClose);

        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await channel.WriteAsync(new byte[10]));
    }

    [Fact]
    public void WriteInitFrame_BuildsCorrectFrame()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);
        byte[] channelIdBytes = "test-channel"u8.ToArray();

        channel.WriteInitFrame(channelIdBytes);

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Init, header.Flags);
        Assert.Equal(channelIdBytes.Length, header.PayloadLength);

        var payload = ready.Span.Slice(FrameHeader.Size, header.PayloadLength).ToArray();
        Assert.Equal(channelIdBytes, payload);
    }

    [Fact]
    public void WriteFinFrame_BuildsHeaderOnly()
    {
        var channel = CreateChannel();

        channel.WriteFinFrame();

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);
        Assert.Equal(0, header.PayloadLength);
        Assert.Equal(FrameHeader.Size, ready.Length);
    }

    [Fact]
    public void WriteAckFrame_Builds4BytePayload()
    {
        var channel = CreateChannel();

        channel.WriteAckFrame(12345);

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Ack, header.Flags);
        Assert.Equal(4, header.PayloadLength);
    }

    [Fact]
    public async Task OnAck_FreesSlabSpace()
    {
        // Use a small slab to test slab compaction
        var channel = CreateChannel(slabSize: 256);

        // Fill most of the slab
        await channel.WriteAsync(new byte[100]);
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // Acknowledge the data — frees space
        channel.OnAck(ready.Length);

        // Should be able to write again
        await channel.WriteAsync(new byte[100]);
    }

    [Fact]
    public void PrepareReplay_ResetsSentToAcked()
    {
        var channel = CreateChannel();

        channel.WriteInitFrame("ch"u8);
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // MarkSent now also advances ackedPos (no replay buffer without reconnection)
        // So PrepareReplay resets sentPos to ackedPos, which equals sentPos — nothing to replay
        channel.PrepareReplay();

        var replayed = channel.TakeReady();
        Assert.True(replayed.IsEmpty);
    }

    [Fact]
    public async Task Stats_TrackBytesSentAndFrames()
    {
        var channel = CreateChannel();

        await channel.WriteAsync(new byte[50]);
        await channel.WriteAsync(new byte[30]);

        Assert.Equal(80, Volatile.Read(ref channel.Stats._bytesSent));
        Assert.Equal(2, Volatile.Read(ref channel.Stats._framesSent));
    }

    [Fact]
    public async Task WriteAsync_ZeroBytes_IsNoop()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);

        await channel.WriteAsync(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0, router.NotifyCount);
        Assert.True(channel.TakeReady().IsEmpty);
    }

    [Fact]
    public async Task CloseAsync_SendsFinAndTransitionsState()
    {
        var channel = CreateChannel();

        await channel.CloseAsync();

        Assert.Equal(ChannelState.Closing, channel.State);
        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);
    }

    [Fact]
    public void SetClosed_RaisesOnClosedEvent()
    {
        var channel = CreateChannel();
        ChannelCloseReason? capturedReason = null;
        channel.Closed += (_, e) => capturedReason = e.Reason;

        channel.SetClosed(ChannelCloseReason.RemoteFin);

        Assert.Equal(ChannelCloseReason.RemoteFin, capturedReason);
        Assert.Equal(ChannelState.Closed, channel.State);
    }
}
