using System.Buffers.Binary;

namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// Isolate the sub-mux data cap: 256 × 256-byte messages = 65,536 bytes
/// (exactly 64KB) is the boundary.  Messages beyond this window never
/// arrive at the reader even when the writer calls DisposeAsync to
/// flush and signal EOF.
/// </summary>
public class SubMuxDataCapIsolationTests
{
    [Fact]
    public async Task SubMux_Direct_1000Messages_AllArrive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start(); meshB.Start();

        var (muxA, muxB, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        meshA.AddNeighbor("B", muxA);
        meshB.AddNeighbor("A", muxB);
        await meshA.WaitForReadyAsync(cts.Token);
        await meshB.WaitForReadyAsync(cts.Token);

        var acceptAtB = Task.Run(async () =>
        {
            await foreach (var inc in meshB.AcceptMultiplexersAsync(cts.Token))
                return inc;
            throw new Exception("never accepted");
        });

        var subA = await meshA.OpenMultiplexerAsync("B", "test", cts.Token);
        var atB = await acceptAtB;

        var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "stream" });
        var reader = atB.Multiplexer.AcceptChannel("stream");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        // 1,000 messages × 256 bytes = 256KB — well past the 64KB cap.
        // DisposeAsync flushes; reader sees EOF.  If the sub-mux silently
        // drops data beyond 64KB, this assertion fails.
        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        var received = new List<long>();
        var buf = new byte[65536]; int off = 0, cnt = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(off + cnt), cts.Token);
            if (n == 0) break;
            cnt += n;
            while (cnt >= 256)
            {
                received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off, 8)));
                off += 256; cnt -= 256;
            }
            if (off > 0 && cnt > 0) { Array.Copy(buf, off, buf, 0, cnt); off = 0; }
        }

        // THE BUG: only 256 messages arrive — the rest are lost.
        Assert.Equal(1000, received.Count);
        for (int i = 0; i < received.Count; i++)
            Assert.Equal((long)i, received[i]);

        await reader.DisposeAsync(); await subA.DisposeAsync(); await atB.Multiplexer.DisposeAsync();
        await muxA.DisposeAsync(); await muxB.DisposeAsync();
        await meshA.DisposeAsync(); await meshB.DisposeAsync();
    }
}
