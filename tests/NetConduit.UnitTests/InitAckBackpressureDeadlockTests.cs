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
    public async Task FirstWrite_AtMultipleSizes_DoesNotDeadlock()
    {
        // Test writes at slabSize - headerSize and 2 * slabSize
        // to cover various boundary conditions around the INIT frame occupancy.
        int slabSize = FrameConstants.MinSlabSize; // 64 KiB
        int headerSize = FrameConstants.HeaderSize;

        int[] payloadSizes = [
            slabSize - headerSize,       // exactly fills slab with INIT + DATA
            slabSize / 2,                // well within single frame
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

            // Concurrent reader to drain data and send ACKs back to writer
            var buf = new byte[payloadSize];
            int totalRead = 0;
            var readTask = Task.Run(async () =>
            {
                while (totalRead < payloadSize)
                {
                    int n = await read.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref totalRead, n);
                }
            }, cts.Token);

            await write.WriteAsync(data, cts.Token);
            await write.DisposeAsync();
            await readTask;

            Assert.Equal(payloadSize, Volatile.Read(ref totalRead));
            Assert.All(buf, b => Assert.Equal(0xEF, b));

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitAck_ReportsCorrectPosition_EnablingFullSlabWrite()
    {
        // Verify that the INIT-ACK reports the correct drained position
        // (including INIT frame bytes), enabling subsequent full-slab writes.
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

        // After the INIT-ACK, the writer should have seen _ackedPos > INIT frame bytes.
        // We verify this by writing the maximum single-frame payload.
        int maxPayload = slabSize - FrameConstants.HeaderSize;
        var data = new byte[maxPayload];
        Array.Fill(data, (byte)0xAB);

        // Concurrent reader
        var buf = new byte[maxPayload];
        int totalRead = 0;
        var readTask = Task.Run(async () =>
        {
            while (totalRead < maxPayload)
            {
                int n = await read.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                Interlocked.Add(ref totalRead, n);
            }
        }, cts.Token);

        await write.WriteAsync(data, cts.Token);
        await write.DisposeAsync();
        await readTask;

        Assert.Equal(maxPayload, Volatile.Read(ref totalRead));
        Assert.All(buf, b => Assert.Equal(0xAB, b));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
