using NetConduit.Constants;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for the INIT-ACK backpressure deadlock:
/// when the INIT-ACK always reports position 0, the writer's INIT frame
/// slot never compacts and the first DATA frame at slab boundary deadlocks.
/// </summary>
public sealed class InitAckBackpressureDeadlockTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair(int slabSize)
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize },
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize },
        });
        return (client, server);
    }

    [Fact]
    public async Task FirstWrite_AtPeerMaxPayloadBoundary_DoesNotDeadlock()
    {
        // Use minimum slab size (64 KiB) to make the deadlock condition
        // most likely to trigger. FrameConstants.HeaderSize = 8 bytes.
        int slabSize = FrameConstants.MinSlabSize; // 64 KiB
        var (client, server) = CreatePair(slabSize);
        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        using var cts = new CancellationTokenSource(TestTimeout);

        var write = client.OpenChannel(new ChannelOptions { ChannelId = "boundary", SlabSize = slabSize });
        var read = await server.AcceptChannelAsync("boundary", cts.Token);
        await write.WaitForReadyAsync(cts.Token);

        // First write = max single-frame payload (slabSize - FrameHeader.Size)
        int maxPayload = slabSize - FrameConstants.HeaderSize;
        var data = new byte[maxPayload];
        Array.Fill(data, (byte)0xCD);

        // This write must not deadlock — it must complete within timeout
        await write.WriteAsync(data, cts.Token);

        // Verify the data arrives on the reader side
        var buf = new byte[maxPayload];
        int totalRead = 0;
        while (totalRead < maxPayload)
        {
            int n = await read.ReadAsync(buf.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(maxPayload, totalRead);
        Assert.All(buf, b => Assert.Equal(0xCD, b));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task FirstWrite_AtMultipleSizesNearBoundary_DoesNotDeadlock()
    {
        // Test writes at slabSize - headerSize, slabSize + 1, and slabSize * 2
        // to cover various boundary conditions around the INIT frame occupancy.
        int slabSize = FrameConstants.MinSlabSize; // 64 KiB
        int headerSize = FrameConstants.HeaderSize;

        int[] payloadSizes = [
            slabSize - headerSize,       // exactly fills slab with INIT + DATA
            slabSize,                    // slightly over single frame (forces split)
            slabSize * 2,                // multi-frame
        ];

        foreach (int payloadSize in payloadSizes)
        {
            var (client, server) = CreatePair(slabSize);
            client.Start();
            server.Start();
            await Task.WhenAll(
                client.WaitForReadyAsync().WaitAsync(TestTimeout),
                server.WaitForReadyAsync().WaitAsync(TestTimeout));

            using var cts = new CancellationTokenSource(TestTimeout);

            string channelId = $"ch-{payloadSize}";
            var write = client.OpenChannel(new ChannelOptions { ChannelId = channelId, SlabSize = slabSize });
            var read = await server.AcceptChannelAsync(channelId, cts.Token);
            await write.WaitForReadyAsync(cts.Token);

            var data = new byte[payloadSize];
            Array.Fill(data, (byte)0xEF);

            await write.WriteAsync(data, cts.Token);

            var buf = new byte[payloadSize];
            int totalRead = 0;
            while (totalRead < payloadSize)
            {
                int n = await read.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }

            Assert.Equal(payloadSize, totalRead);
            Assert.All(buf, b => Assert.Equal(0xEF, b));

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitAck_AdvancesPeerSlabWindow_BeyondInitFrame()
    {
        // Deterministic test: verify that after INIT-ACK, the writer's
        // acked position has advanced past the INIT frame bytes, meaning
        // the INIT slot in the slab can be compacted.
        int slabSize = FrameConstants.MinSlabSize; // 64 KiB
        var (client, server) = CreatePair(slabSize);
        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        using var cts = new CancellationTokenSource(TestTimeout);

        var write = client.OpenChannel(new ChannelOptions { ChannelId = "acktest", SlabSize = slabSize });
        var read = await server.AcceptChannelAsync("acktest", cts.Token);
        await write.WaitForReadyAsync(cts.Token);

        // Write a small payload to flush the INIT through and wait for the ACK
        var smallData = new byte[8];
        await write.WriteAsync(smallData, cts.Token);
        await Task.Delay(100, cts.Token);

        // After the INIT-ACK, the writer should have seen _ackedPos > INIT frame bytes.
        // We verify this indirectly: a subsequent full-slab write should not deadlock.
        int maxPayload = slabSize - FrameConstants.HeaderSize;
        var bigData = new byte[maxPayload];
        await write.WriteAsync(bigData, cts.Token);

        var buf = new byte[maxPayload];
        int totalRead = 0;
        while (totalRead < maxPayload)
        {
            int n = await read.ReadAsync(buf.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(maxPayload, totalRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
