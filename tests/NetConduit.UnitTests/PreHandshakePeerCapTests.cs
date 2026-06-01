using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Pre-handshake WriteAsync must use a conservative peer-recv-payload cap
/// (MinSlabSize) instead of the default 1 MiB. Otherwise a caller can buffer
/// a frame that is wire-legal for the local 1 MiB default but exceeds the
/// peer's actual advertised cap once the handshake lands. The drainer then
/// ships an oversize frame, the peer faults BufferInSlab with
/// MultiplexerException(ProtocolError), and every reconnect attempt replays
/// the same frame until MaxAutoReconnectAttempts is exhausted.
/// </summary>
public sealed class PreHandshakePeerCapTests
{
    private sealed class TestRouter : IChannelOwner
    {
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(uint channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(uint channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
        // Advertise the maximum so post-handshake writes are unconstrained by
        // the peer cap; the only relevant clamp in these tests is the
        // pre-handshake conservative one.
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
    }

    private static WriteChannel CreateChannel(int slabSize)
    {
        var router = new TestRouter();
        var channel = new WriteChannel(
            channelId: "test-358",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(60),
            owner: router);
        channel.MarkOpen();
        return channel;
    }

    [Fact]
    public async Task WriteAsync_PreHandshake_PayloadExceedsMinSlabCap_SplitsIntoMinSlabFrames()
    {
        // Local slab is 1 MiB so the local-only check would pass; the peer cap
        // must be the per-frame binding constraint pre-handshake.
        var channel = CreateChannel(slabSize: FrameConstants.DefaultSlabSize);

        // 900 KiB — the example payload from issue.
        byte[] payload = new byte[900 * 1024];

        var writeTask = channel.WriteAsync(payload).AsTask();
        int stagedPayloadBytes = 0;
        int maxObservedPayload = 0;
        while (!writeTask.IsCompleted)
        {
            var ready = channel.TakeReady();
            if (ready.IsEmpty)
            {
                await Task.Delay(10);
                continue;
            }

            var result = CountDataPayloadBytes(ready.Span);
            stagedPayloadBytes += result.TotalBytes;
            maxObservedPayload = Math.Max(maxObservedPayload, result.MaxPayloadBytes);
            channel.MarkSent(ready.Length);
        }

        var remaining = channel.TakeReady();
        if (!remaining.IsEmpty)
        {
            var result = CountDataPayloadBytes(remaining.Span);
            stagedPayloadBytes += result.TotalBytes;
            maxObservedPayload = Math.Max(maxObservedPayload, result.MaxPayloadBytes);
            channel.MarkSent(remaining.Length);
        }

        await writeTask;

        Assert.Equal(payload.Length, stagedPayloadBytes);
        Assert.True(maxObservedPayload <= FrameConstants.MinSlabSize - FrameHeader.Size);
    }

    private static (int TotalBytes, int MaxPayloadBytes) CountDataPayloadBytes(ReadOnlySpan<byte> frames)
    {
        int total = 0;
        int maxPayload = 0;
        int position = 0;
        while (position < frames.Length)
        {
            var header = FrameHeader.Parse(frames[position..]);
            Assert.Equal(FrameFlags.Data, header.Flags);
            total += header.PayloadLength;
            maxPayload = Math.Max(maxPayload, header.PayloadLength);
            position += header.FrameSize;
        }

        Assert.Equal(frames.Length, position);
        return (total, maxPayload);
    }

    [Fact]
    public async Task WriteAsync_PreHandshake_PayloadAtMinSlabBoundary_Succeeds()
    {
        var channel = CreateChannel(slabSize: FrameConstants.DefaultSlabSize);

        // MinSlabSize minus the frame header is the largest payload that fits
        // in any peer's advertised receive slab.
        byte[] payload = new byte[FrameConstants.MinSlabSize - FrameHeader.Size];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size + payload.Length, ready.Length);
    }

    [Fact]
    public async Task WriteAsync_PostHandshake_OversizePayload_Succeeds()
    {
        // After MarkConnected the channel trusts the router's advertised cap
        // (MaxSlabSize in this test), so a payload that would have been
        // rejected pre-handshake now fits.
        var channel = CreateChannel(slabSize: FrameConstants.DefaultSlabSize);
        channel.MarkConnected();

        byte[] payload = new byte[900 * 1024];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size + payload.Length, ready.Length);
    }

    [Fact]
    public async Task WriteAsync_PostHandshake_ThenDisconnect_StillTrustsNegotiatedCap()
    {
        // After the channel has observed at least one handshake the latch is
        // set and stays set — _owner.PeerMaxRecvPayload is the last
        // negotiated value (or, after reconnect, the freshly renegotiated
        // value), which is safe to trust during the reconnect window. This
        // preserves existing behavior where writes during disconnect buffer
        // into the slab to be drained on reconnect (see
        // DisconnectionDataLossTests.WriteAfterDisconnect_LargeData_Succeeds).
        var channel = CreateChannel(slabSize: FrameConstants.DefaultSlabSize);
        channel.MarkConnected();
        channel.TakeReady();
        channel.MarkDisconnected(DisconnectReason.TransportError);

        byte[] payload = new byte[900 * 1024];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size + payload.Length, ready.Length);
    }
}
