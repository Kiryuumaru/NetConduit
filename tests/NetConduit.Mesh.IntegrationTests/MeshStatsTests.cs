namespace NetConduit.Mesh.IntegrationTests;

public class MeshStatsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RoutesOpened_IncrementsOnSuccessfulOpen()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start(); meshB.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        long before = meshA.Stats.RoutesOpened;
        await using var sub = await meshA.OpenMultiplexerAsync("B", "rpc-1", cts.Token);
        long after = meshA.Stats.RoutesOpened;

        Assert.True(after > before);
        Assert.True(meshA.Stats.ActiveSubMultiplexers >= 1);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task TopologyMessagesSent_IncrementsOnAddNeighbor()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start(); meshB.Start();

        Assert.Equal(0L, meshA.Stats.TopologyMessagesSent);

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        // Sent at least one topology message during the initial exchange.
        for (int i = 0; i < 50 && meshA.Stats.TopologyMessagesSent == 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.True(meshA.Stats.TopologyMessagesSent > 0);
        Assert.True(meshB.Stats.TopologyMessagesReceived > 0);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task RelayBytesForwarded_MatchesPayloadAtRelay()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBC_B, muxBC_C, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        await using var meshC = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "C" });
        meshA.Start(); meshB.Start(); meshC.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        meshB.AddNeighbor("C", muxBC_B);
        meshC.AddNeighbor("B", muxBC_C);

        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "C") aReady.TrySetResult(); };
        await aReady.Task.WaitAsync(cts.Token);

        long beforeBytes = meshB.Stats.RelayBytesForwarded;
        long beforeRelays = meshB.Stats.ActiveRelays;

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var r in meshC.AcceptMultiplexersAsync(cts.Token))
            {
                return r;
            }
            throw new InvalidOperationException();
        }, cts.Token);

        await using var subA = await meshA.OpenMultiplexerAsync("C", "rpc-1", cts.Token);
        var routed = await acceptTask.WaitAsync(cts.Token);
        await routed.Multiplexer.WaitForReadyAsync(cts.Token);

        var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "data" });
        var reader = routed.Multiplexer.AcceptChannel("data");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        byte[] payload = new byte[8192];
        new Random(7).NextBytes(payload);
        await writer.WriteAsync(payload, cts.Token);

        var buf = new byte[payload.Length];
        int total = 0;
        while (total < payload.Length)
        {
            int n = await reader.ReadAsync(buf.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(payload.Length, total);

        // The relay must have forwarded at least the payload bytes (sub-mux framing adds more).
        Assert.True(meshB.Stats.RelayBytesForwarded >= beforeBytes + payload.Length,
            $"Expected >= {beforeBytes + payload.Length}, got {meshB.Stats.RelayBytesForwarded}");

        await writer.DisposeAsync();
        await reader.DisposeAsync();
        await routed.Multiplexer.DisposeAsync();
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
        await muxBC_B.DisposeAsync();
        await muxBC_C.DisposeAsync();
    }
}
