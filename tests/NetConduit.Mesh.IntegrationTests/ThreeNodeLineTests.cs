namespace NetConduit.Mesh.IntegrationTests;

public class ThreeNodeLineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RelayThroughMiddleNode_DeliversBytesEndToEnd()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Topology: A <-> B <-> C
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBC_B, muxBC_C, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        await using var meshC = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "C" });
        meshA.Start();
        meshB.Start();
        meshC.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        meshB.AddNeighbor("C", muxBC_B);
        meshC.AddNeighbor("B", muxBC_C);

        // Wait until A sees C reachable (2 hops via B).
        var aReachableC = new TaskCompletionSource<NodeReachableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "C") aReachableC.TrySetResult(e); };
        var cReachableA = new TaskCompletionSource<NodeReachableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshC.NodeReachable += (_, e) => { if (e.NodeId == "A") cReachableA.TrySetResult(e); };

        var evAC = await aReachableC.Task.WaitAsync(cts.Token);
        var evCA = await cReachableA.Task.WaitAsync(cts.Token);
        Assert.Equal(2, evAC.HopCount);
        Assert.Equal(2, evCA.HopCount);

        // Accept at C and open A->C.
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var routed in meshC.AcceptMultiplexersAsync(cts.Token))
            {
                return routed;
            }
            throw new InvalidOperationException("no routed mux");
        }, cts.Token);

        await using var subA = await meshA.OpenMultiplexerAsync("C", "rpc-1", cts.Token);

        var routedC = await acceptTask.WaitAsync(cts.Token);
        Assert.Equal("A", routedC.SourceNodeId);
        Assert.Equal("rpc-1", routedC.MultiplexerId);

        await routedC.Multiplexer.WaitForReadyAsync(cts.Token);

        var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "data" });
        var reader = routedC.Multiplexer.AcceptChannel("data");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        byte[] payload = new byte[16 * 1024];
        new Random(42).NextBytes(payload);
        await writer.WriteAsync(payload, cts.Token);

        var buf = new byte[payload.Length];
        int total = 0;
        while (total < payload.Length)
        {
            int n = await reader.ReadAsync(buf.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(payload, buf[..total]);

        // Stats: B should have forwarded bytes both directions (at least the payload).
        await MeshTestAssertions.AssertRelayBytesForwardedAtLeastAsync(meshB, payload.Length, cts.Token);

        await writer.DisposeAsync();
        await reader.DisposeAsync();
        await routedC.Multiplexer.DisposeAsync();

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
        await muxBC_B.DisposeAsync();
        await muxBC_C.DisposeAsync();
    }

    [Fact]
    public async Task MaxHopsExceeded_OpenThrowsMeshRoutingException()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBC_B, muxBC_C, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions
        {
            NodeId = "A",
            MaxHops = 1,
            MaxRouteRetries = 0,
            RouteTimeout = TimeSpan.FromSeconds(2),
        });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        await using var meshC = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "C" });
        meshA.Start();
        meshB.Start();
        meshC.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        meshB.AddNeighbor("C", muxBC_B);
        meshC.AddNeighbor("B", muxBC_C);

        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token), meshC.WaitForReadyAsync(cts.Token));

        // Give topology a moment to propagate to A (B advertises that C is its neighbor).
        // But with MaxHops=1, A cannot route to C even after convergence.
        await Task.Delay(200, cts.Token);

        // With MaxHops = 1, A cannot route to C even after topology converges.
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var sub = await meshA.OpenMultiplexerAsync("C", "rpc-1", cts.Token);
        });
        Assert.True(
            ex is MeshRoutingException ||
            ex.InnerException is MeshRoutingException ||
            ex is ObjectDisposedException ||
            ex is InvalidOperationException,
            $"Expected MeshRoutingException-bearing failure, got {ex.GetType().Name}: {ex.Message}");

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
        await muxBC_B.DisposeAsync();
        await muxBC_C.DisposeAsync();
    }
}
