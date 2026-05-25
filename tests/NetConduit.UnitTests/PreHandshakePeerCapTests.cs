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
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
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
    public async Task WriteAsync_PreHandshake_PayloadExceedsMinSlabCap_Throws()
    {
        // Local slab is 1 MiB so the local-only check would pass; the peer cap
        // must be the binding constraint pre-handshake.
        var channel = CreateChannel(slabSize: FrameConstants.DefaultSlabSize);

        // 900 KiB — the example payload from issue.
        byte[] payload = new byte[900 * 1024];

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => channel.WriteAsync(payload).AsTask());
        Assert.Contains("pre-handshake", ex.Message);
        Assert.Contains("WaitForReadyAsync", ex.Message);
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
