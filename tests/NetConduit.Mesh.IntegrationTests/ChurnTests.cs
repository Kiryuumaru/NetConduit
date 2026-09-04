using System.Runtime.CompilerServices;

namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// Stress tests covering rapid neighbor churn, repeated open/dispose cycles, and
/// memory pressure regressions. These run in CI on the default test pool.
/// </summary>
public class ChurnTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task AddRemoveNeighbor_RapidCycles_ConvergesAndCleansUp()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();

        const int Cycles = 25;
        var unreachableEvents = 0;
        meshA.NodeUnreachable += (_, _) => Interlocked.Increment(ref unreachableEvents);

        var muxes = new List<(StreamMultiplexer A, StreamMultiplexer B)>(Cycles);
        for (int i = 0; i < Cycles; i++)
        {
            var (muxA, muxB, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
            muxes.Add((muxA, muxB));

            meshA.AddNeighbor("B", muxA);
            meshB.AddNeighbor("A", muxB);
            await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

            meshA.RemoveNeighbor("B");
            meshB.RemoveNeighbor("A");
        }

        // Final pair stays so we can open one routed mux to prove the mesh is healthy.
        var (finalA, finalB, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        muxes.Add((finalA, finalB));
        meshA.AddNeighbor("B", finalA);
        meshB.AddNeighbor("A", finalB);
        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        await using (var routed = await meshA.OpenMultiplexerAsync("B", "final", cts.Token))
        {
            await routed.WaitForReadyAsync(cts.Token);
        }

        // Sub-multiplexer count must drain after disposal.
        for (int i = 0; i < 100 && meshA.Stats.ActiveSubMultiplexers > 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(0L, meshA.Stats.ActiveSubMultiplexers);
        Assert.Equal(0L, meshA.Stats.ActiveRelays);

        foreach (var (a, b) in muxes)
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenDisposeRoutedMux_ManyCycles_StatsDrainToZero()
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

        const int Iterations = 50;
        long opened = meshA.Stats.RoutesOpened;

        for (int i = 0; i < Iterations; i++)
        {
            var routed = await meshA.OpenMultiplexerAsync("B", $"rpc-{i}", cts.Token);
            await routed.WaitForReadyAsync(cts.Token);
            await routed.DisposeAsync();
        }

        Assert.True(meshA.Stats.RoutesOpened >= opened + Iterations);

        for (int i = 0; i < 250 && meshA.Stats.ActiveSubMultiplexers > 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(0L, meshA.Stats.ActiveSubMultiplexers);
        Assert.Equal(0L, meshA.Stats.ActiveRelays);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task DisposedRoutedMuxes_AreReleasedByGc()
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

        var refs = await CreateAndDiscardRoutedMuxesAsync(meshA, cts.Token);

        // Let any inbox channels / forwarders settle before GC.
        for (int i = 0; i < 100 && meshA.Stats.ActiveSubMultiplexers > 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(0L, meshA.Stats.ActiveSubMultiplexers);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (refs.All(r => !r.TryGetTarget(out _)))
            {
                break;
            }
            await Task.Delay(50, cts.Token);
        }

        int alive = refs.Count(r => r.TryGetTarget(out _));
        Assert.True(alive == 0, $"Expected all routed muxes to be GC'd, but {alive}/{refs.Count} are still alive.");

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference<IStreamMultiplexer>>> CreateAndDiscardRoutedMuxesAsync(
        MeshMultiplexer mesh, CancellationToken ct)
    {
        const int Count = 20;
        var refs = new List<WeakReference<IStreamMultiplexer>>(Count);
        for (int i = 0; i < Count; i++)
        {
            var routed = await mesh.OpenMultiplexerAsync("B", $"gc-{i}", ct);
            await routed.WaitForReadyAsync(ct);
            refs.Add(new WeakReference<IStreamMultiplexer>(routed));
            await routed.DisposeAsync();
        }
        return refs;
    }

    [Fact]
    public async Task ConcurrentOpens_AllSucceedAndDrain()
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

        const int Parallelism = 16;
        var tasks = new Task<IStreamMultiplexer>[Parallelism];
        for (int i = 0; i < Parallelism; i++)
        {
            int id = i;
            tasks[i] = Task.Run(() => meshA.OpenMultiplexerAsync("B", $"par-{id}", cts.Token), cts.Token);
        }
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.NotNull(r));

        await Task.WhenAll(results.Select(r => r.WaitForReadyAsync(cts.Token)));
        foreach (var r in results)
        {
            await r.DisposeAsync();
        }

        for (int i = 0; i < 250 && meshA.Stats.ActiveSubMultiplexers > 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(0L, meshA.Stats.ActiveSubMultiplexers);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }
}
