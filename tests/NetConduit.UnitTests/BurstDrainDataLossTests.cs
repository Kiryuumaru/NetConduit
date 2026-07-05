using System.Buffers.Binary;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for the burst-then-drain data loss scenarios from #546.
/// Two distinct bugs:
/// 1. Burst-drain truncation: when the writer bursts past the peer's slab
///    capacity and disposes before the reader drains, the flow-control proxy
///    in WriteFrameAsync does not subtract _ackedPos from _writePos, causing
///    the peer-free calculation to over-state available buffer after
///    compaction. The writer admits frames past the peer's capacity, the peer
///    throws ProtocolError on BufferInSlab overflow, the transport faults,
///    and the dispose path drops undrained data from the writer's slab.
/// 2. FIN-on-Closed race: TryQueuePendingFinLocked rejected Closed state
///    (fixed in same change).
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
    /// Exact reproduction of issue #546: write 1000 messages then drain.
    /// With replay disabled, MarkSent auto-advances _ackedPos, compaction
    /// reduces _writePos, and the peerFree formula overstates available
    /// peer buffer — admitting frames past the peer's slab capacity.
    /// The peer throws ProtocolError on overflow, transport faults, and
    /// DisposeAsync drops undrained data from the write slab.
    /// </summary>
    [Fact]
    public async Task BurstThenDrain_AllMessagesArrive()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writer = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "burst",
            SendTimeout = TimeSpan.FromSeconds(30),
        });

        // Accept the channel so INIT is processed, but don't drain the
        // reader yet — the read channel buffers via direct delivery but
        // the consumer never reads, so _consumedPos stays at 0.
        var acceptTask = server.AcceptChannelAsync("burst");

        const int messageCount = 1000;
        const int messageSize = 256;
        for (int i = 0; i < messageCount; i++)
        {
            var msg = new byte[messageSize];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg);
        }

        await writer.DisposeAsync();

        var reader = await acceptTask;
        var received = new List<long>();
        var buf = new byte[messageSize];
        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < messageSize)
            {
                int n = await reader.ReadAsync(buf.AsMemory(bytesRead, messageSize - bytesRead));
                if (n == 0) goto drainDone;
                bytesRead += n;
            }
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }
    drainDone:
        Assert.Equal(messageCount, received.Count);
    }

    /// <summary>
    /// FIN-on-Closed race: Fill the slab so FIN cannot fit when CloseAsync runs.
    /// Call SetClosed (simulating DisposeAsync's post-close state transition),
    /// then drain via MarkSent. With the fix, TryQueuePendingFinLocked must allow
    /// queuing FIN even in Closed state.
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