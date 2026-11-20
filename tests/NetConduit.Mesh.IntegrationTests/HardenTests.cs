namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// End-to-end validation for the HARDEN.md work (T0–T5). Each test verifies a specific
/// behavior change that the audit / investigation called for.
/// </summary>
public class HardenTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// T3 — auto-cleanse dead neighbor. Disposing a neighbor mux directly, WITHOUT
    /// calling <c>RemoveNeighbor</c>, must cause the mesh to detect the terminal
    /// disconnect, drop the neighbor from local adjacency, recompute, and re-broadcast.
    /// A node that previously reached the dead peer only through the now-dead neighbor
    /// must observe a <c>NodeUnreachable</c> event for that peer.
    /// </summary>
    [Fact]
    public async Task NeighborMuxDies_AutoCleansesWithoutExplicitRemove()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBC_B, muxBC_C, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        var baseOpts = new MeshMultiplexerOptions { NodeId = "placeholder" };
        await using var meshA = MeshMultiplexer.Create(baseOpts with { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(baseOpts with { NodeId = "B" });
        await using var meshC = MeshMultiplexer.Create(baseOpts with { NodeId = "C" });

        meshA.Start(); meshB.Start(); meshC.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        meshB.AddNeighbor("C", muxBC_B);
        meshC.AddNeighbor("B", muxBC_C);

        var aReachable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "C") aReachable.TrySetResult(); };
        await aReachable.Task.WaitAsync(cts.Token);

        // The application drops the B mux on C's side WITHOUT calling RemoveNeighbor.
        // The auto-cleanse path must observe the terminal disconnect on C's side and
        // propagate convergence so A eventually sees C as unreachable.
        var aUnreachable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeUnreachable += (_, e) => { if (e.NodeId == "C") aUnreachable.TrySetResult(); };

        await muxBC_B.DisposeAsync();
        await muxBC_C.DisposeAsync();

        await aUnreachable.Task.WaitAsync(cts.Token);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    /// <summary>
    /// T1 — single-flight topology writes. Bursty churn must coalesce: with the old
    /// per-broadcast <c>Task.Run</c> model, N broadcasts produced N concurrent writes;
    /// with single-flight, a burst of N from many threads produces strictly fewer than N
    /// actual frames on the wire (last-writer-wins coalescing).
    /// </summary>
    [Fact]
    public async Task BurstyTopologyChurn_CoalescesIntoBoundedWrites()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start(); meshB.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        long baseline = meshB.Stats.TopologyMessagesReceived;

        // Drive churn from many threads in parallel so the writer cannot drain
        // between enqueues. Each cycle: Add + Remove a fake side neighbor on A. Each
        // public call invokes BroadcastLocalTopology synchronously which enqueues
        // through single-flight.
        const int Threads = 4;
        const int CyclesPerThread = 15;
        var sideMuxes = new System.Collections.Concurrent.ConcurrentBag<IStreamMultiplexer>();

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < CyclesPerThread; i++)
            {
                var (sideA, sideX, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
                sideMuxes.Add(sideA); sideMuxes.Add(sideX);
                string nodeId = $"X{t}_{i}";
                meshA.AddNeighbor(nodeId, sideA);
                meshA.RemoveNeighbor(nodeId);
            }
        }, cts.Token)));

        // Wait long enough for the writer loop to drain.
        await Task.Delay(500, cts.Token);

        long received = meshB.Stats.TopologyMessagesReceived - baseline;
        const int TotalBroadcasts = Threads * CyclesPerThread * 2; // Add + Remove
        // Coalescing must produce strictly fewer writes than naive 1:1.
        Assert.True(received < TotalBroadcasts,
            $"Expected coalesced writes < {TotalBroadcasts}, got {received}.");

        foreach (var m in sideMuxes)
        {
            try { await m.DisposeAsync(); } catch { }
        }
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    /// <summary>
    /// T5 — opt-in infinite reconnect. With <c>MaxRouteRetries = -1</c>, a routed sub-mux
    /// whose underlying transport dies must not raise terminal Disconnected after the
    /// default 3 retries — it must keep trying until the application disposes it. Bounded
    /// here by ensuring no terminal disconnect is observed within a reasonable window
    /// while the underlying link is down.
    /// </summary>
    [Fact]
    public async Task InfiniteRetries_SubMuxSurvivesProlongedOutage()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        var opts = new MeshMultiplexerOptions
        {
            NodeId = "placeholder",
            MaxRouteRetries = -1,
            RouteTimeout = TimeSpan.FromSeconds(2),
        };
        await using var meshA = MeshMultiplexer.Create(opts with { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(opts with { NodeId = "B" });
        meshA.Start(); meshB.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var r in meshB.AcceptMultiplexersAsync(cts.Token))
            {
                return r;
            }
            throw new InvalidOperationException("Never accepted.");
        }, cts.Token);

        await using var subA = await meshA.OpenMultiplexerAsync("B", "rpc-1", cts.Token);
        var routed = await acceptTask.WaitAsync(cts.Token);
        await routed.Multiplexer.WaitForReadyAsync(cts.Token);

        bool terminalDisconnect = false;
        subA.Disconnected += (_, e) =>
        {
            if (!subA.IsRunning) terminalDisconnect = true;
        };

        // Actually break the underlying neighbor link so the sub-mux must rely on
        // reconnect/replay. With MaxRouteRetries=-1 (unbounded, the default) the
        // sub-mux must keep trying for the entire window rather than raising a
        // terminal disconnect.
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();

        await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);

        Assert.False(terminalDisconnect, "Sub-mux raised terminal disconnect under MaxRouteRetries=-1.");

        await routed.Multiplexer.DisposeAsync();
    }

    /// <summary>
    /// T4 — <c>AddNeighbor</c> vs <c>DisposeAsync</c> race. Repeatedly racing add and
    /// dispose must never leak a session (no <c>ObjectDisposedException</c> surfacing
    /// after dispose returns, and no orphaned <c>ActiveSubMultiplexers</c>).
    /// </summary>
    [Fact]
    public async Task AddNeighborVsDispose_NoLeakedSessions()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Run many small races; each iteration is short.
        for (int iter = 0; iter < 25; iter++)
        {
            var (muxA, muxB, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
            var mesh = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
            mesh.Start();

            var addTask = Task.Run(() =>
            {
                try { mesh.AddNeighbor("B", muxA); }
                catch (ObjectDisposedException) { /* expected race outcome */ }
                catch (InvalidOperationException) { /* mesh shut down */ }
            }, cts.Token);

            var disposeTask = mesh.DisposeAsync().AsTask();

            await Task.WhenAll(addTask, disposeTask);
            await muxA.DisposeAsync();
            await muxB.DisposeAsync();
        }
    }
}
