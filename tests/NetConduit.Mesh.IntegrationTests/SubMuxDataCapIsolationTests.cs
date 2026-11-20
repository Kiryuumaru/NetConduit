using System.Buffers.Binary;

namespace NetConduit.Mesh.IntegrationTests;

public class SubMuxDataCapIsolationTests
{
    [Fact]
    public async Task SubMux_Direct_1000Messages_AllArrive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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

        // Match PR #544 test: concurrent reader drains messages as they arrive.
        var received = new List<long>();
        var readTask = Task.Run(async () =>
        {
            byte[] buf = new byte[256];
            while (true)
            {
                int n = await reader.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                long seq = BinaryPrimitives.ReadInt64LittleEndian(buf);
                lock (received) received.Add(seq);
            }
        });

        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();
        await readTask;

        Assert.Equal(1000, received.Count);

        await reader.DisposeAsync(); await subA.DisposeAsync(); await atB.Multiplexer.DisposeAsync();
        await muxA.DisposeAsync(); await muxB.DisposeAsync();
        await meshA.DisposeAsync(); await meshB.DisposeAsync();
    }
}
