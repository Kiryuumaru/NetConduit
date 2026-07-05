using System.Buffers.Binary;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

// Two separate test classes for two distinct data-loss paths from #546.

/// <summary>
/// Burst-then-drain: writer bursts past the peer's read-slab capacity because
/// compaction (triggered by MarkSent's no-replay _ackedPos auto-advance)
/// resets _writePos, tricking the peerFree formula into admitting frames past
/// the peer's slab. The peer's BufferInSlab throws ProtocolError, the
/// transport faults, and DisposeAsync drops undrained data.
/// </summary>
public sealed class BurstDrainDataLossTests
{
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
}

/// <summary>
/// FIN-on-Closed race: during DisposeAsync, SetClosed transitions state to
/// Closed before the writer loop drains. TryQueuePendingFinLocked rejected
/// FIN queuing when state was Closed, so the peer saw a hard EOF instead of
/// a graceful FIN-terminated close.
/// </summary>
public sealed class FinOnClosedRaceTests
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