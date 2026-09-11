namespace NetConduit.Mesh.IntegrationTests;

public class TwoNodeMeshTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AddNeighbor_TopologyConverges_NodeReachableFires()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();

        var reachableAtA = new TaskCompletionSource<NodeReachableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "B") reachableAtA.TrySetResult(e); };
        var reachableAtB = new TaskCompletionSource<NodeReachableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshB.NodeReachable += (_, e) => { if (e.NodeId == "A") reachableAtB.TrySetResult(e); };

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await meshA.WaitForReadyAsync(cts.Token);
        await meshB.WaitForReadyAsync(cts.Token);

        var evA = await reachableAtA.Task.WaitAsync(cts.Token);
        var evB = await reachableAtB.Task.WaitAsync(cts.Token);

        Assert.Equal(1, evA.HopCount);
        Assert.Equal(1, evB.HopCount);
        Assert.Equal(1, meshA.ReachableNodeCount);
        Assert.Equal(1, meshB.ReachableNodeCount);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task OpenAndAcceptMultiplexer_RoutesBytesEndToEnd()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        // B side: accept via async enumerable.
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var routed in meshB.AcceptMultiplexersAsync(cts.Token))
            {
                return routed;
            }
            throw new InvalidOperationException("No routed mux arrived.");
        }, cts.Token);

        await using var subA = await meshA.OpenMultiplexerAsync("B", "rpc-1", cts.Token);

        var routedB = await acceptTask.WaitAsync(cts.Token);
        Assert.Equal("A", routedB.SourceNodeId);
        Assert.Equal("rpc-1", routedB.MultiplexerId);

        await routedB.Multiplexer.WaitForReadyAsync(cts.Token);

        // Send bytes A -> B.
        var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "data" });
        var reader = routedB.Multiplexer.AcceptChannel("data");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        byte[] payload = "hello-mesh"u8.ToArray();
        await writer.WriteAsync(payload, cts.Token);

        var buf = new byte[64];
        int total = 0;
        while (total < payload.Length)
        {
            int n = await reader.ReadAsync(buf.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }

        Assert.Equal(payload, buf[..total]);

        await writer.DisposeAsync();
        await reader.DisposeAsync();
        await routedB.Multiplexer.DisposeAsync();

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task RemoveNeighbor_NodeUnreachableFires()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();

        var reachable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unreachable = new TaskCompletionSource<NodeUnreachableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "B") reachable.TrySetResult(); };
        meshA.NodeUnreachable += (_, e) => { if (e.NodeId == "B") unreachable.TrySetResult(e); };

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await reachable.Task.WaitAsync(cts.Token);

        meshA.RemoveNeighbor("B");

        var ev = await unreachable.Task.WaitAsync(cts.Token);
        Assert.Equal("B", ev.NodeId);
        Assert.Equal(0, meshA.ReachableNodeCount);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task AddNeighbor_RejectsSelfAsNeighbor()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, _, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();

        Assert.Throws<ArgumentException>(() => meshA.AddNeighbor("A", muxAB_A));
    }

    [Fact]
    public async Task AddNeighbor_RejectsDuplicate()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();

        meshA.AddNeighbor("B", muxAB_A);
        Assert.Throws<InvalidOperationException>(() => meshA.AddNeighbor("B", muxAB_A));

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task OpenMultiplexer_ToSelfThrows()
    {
        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();

        Assert.Throws<ArgumentException>(() => meshA.OpenMultiplexer("A", "rpc-1"));
        await Task.CompletedTask;
    }
}
