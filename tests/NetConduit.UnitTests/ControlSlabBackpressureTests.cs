using NetConduit.Constants;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

// Regression for issue #291: control-channel slab pressure must not surface
// as a thrown exception out of ReadChannel.ReadAsync nor fault the mux reader
// thread. TryWriteRawFrame returns false on slab-full; IChannelOwner.SendAck
// is propagated through that, and ReadChannel.MaybeSendAck retains its
// unacked accumulator so the next gate crossing retries.
public sealed class ControlSlabBackpressureTests
{
    [Fact]
    public void TryWriteRawFrame_ReturnsFalse_WhenSlabFull_WithoutThrowing()
    {
        var router = new RecordingRouter();
        // Smallest legal slab size so we can fill it quickly with raw frames.
        int slabSize = FrameConstants.MinSlabSize;
        var control = new WriteChannel(
            channelId: "ctrl",
            channelIndex: 0,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: router);
        control.MarkOpen();

        // 17-byte ACK frame: 8-byte header + 8-byte position payload + 1B slack? No — header is 8, payload is 8 -> 16 bytes. Use that.
        byte[] ackFrame = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);

        int accepted = 0;
        int rejected = 0;
        // Push frames until TryWriteRawFrame starts returning false. The writer
        // loop is never invoked in this test, so nothing drains -> compaction
        // can't help -> the slab fills deterministically.
        for (int i = 0; i < (slabSize / ackFrame.Length) + 8; i++)
        {
            if (control.TryWriteRawFrame(ackFrame))
                accepted++;
            else
                rejected++;
        }

        Assert.True(accepted > 0, "At least one frame should fit in an empty slab");
        Assert.True(rejected > 0, "Eventually the slab must reject further frames");
    }

    [Fact]
    public void SendAckRetries_WhenControlChannelTransientlyRejects()
    {
        // ReadChannel.MaybeSendAck must NOT advance its high-water mark when
        // IChannelOwner.SendAck returns false. The next gate crossing should
        // retry with the latest cumulative position.
        var owner = new ToggleableAckOwner { AcceptNext = false };
        int slabSize = FrameConstants.MinSlabSize;
        var read = new ReadChannel(
            channelId: "data",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            owner: owner);
        read.MarkOpen();

        // Drive enough Data frames to cross the ACK gate (slab/16).
        int gate = slabSize / 16;
        byte[] payload = new byte[256];
        int frameBytesPerFrame = FrameHeader.Size + payload.Length;
        int framesToCrossGate = (gate / frameBytesPerFrame) + 2;

        for (int i = 0; i < framesToCrossGate; i++)
            read.ReceivePayload(FrameFlags.Data, payload);

        // Owner refused — no ACK should be recorded.
        Assert.Empty(owner.SentAcks);

        // Drain the buffered bytes so further Data frames can be buffered.
        byte[] sink = new byte[slabSize];
        var t = read.ReadAsync(sink).AsTask();
        Assert.True(t.IsCompleted);

        // Owner now accepts; one more frame past the gate must trigger a retry
        // with the full cumulative position, not just the latest frame's bytes.
        owner.AcceptNext = true;
        for (int i = 0; i < framesToCrossGate; i++)
            read.ReceivePayload(FrameFlags.Data, payload);

        Assert.NotEmpty(owner.SentAcks);
        ulong reportedPosition = owner.SentAcks[^1].Position;
        // Reported position must reflect cumulative bytes, including the
        // frames the previous (refused) gate crossing covered.
        Assert.True(reportedPosition >= (ulong)(frameBytesPerFrame * framesToCrossGate * 2),
            $"Reported position {reportedPosition} must include cumulative bytes from refused-gate frames");
    }

    private sealed class RecordingRouter : IChannelOwner
    {
        public int NotifyCount;
        public void NotifyReady(WriteChannel channel) => Interlocked.Increment(ref NotifyCount);
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
    }

    private sealed class ToggleableAckOwner : IChannelOwner
    {
        public bool AcceptNext;
        public List<(ushort Index, ulong Position)> SentAcks { get; } = [];

        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition)
        {
            if (!AcceptNext) return false;
            SentAcks.Add((channelIndex, consumedPosition));
            return true;
        }
        public void NotifyEventHandlerException(Exception exception) { }
    }
}
