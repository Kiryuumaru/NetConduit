namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// Verifies mesh behavior when an intermediate link fails.
/// </summary>
public class LinkFailoverTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Topology:
    /// <code>
    ///   A --- B --- E
    ///         |     |
    ///         C --- D
    /// </code>
    /// A opens a routed mux to E. Initial path: A -> B -> E.
    /// The application detects the B-E link failing, calls RemoveNeighbor on both
    /// sides, and disposes the underlying mux pair. Topology must converge so that
    /// A can still reach E via A -> B -> C -> D -> E, verified by opening a
    /// brand-new routed sub-multiplexer post-failure.
    /// </summary>
    [Fact]
    public async Task BreakIntermediateLink_TopologyConvergesAndAlternatePathOpens()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBC_B, muxBC_C, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxBE_B, muxBE_E, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxCD_C, muxCD_D, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);
        var (muxDE_D, muxDE_E, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        static MeshMultiplexerOptions OptionsFor(string nodeId) => new()
        {
            NodeId = nodeId,
            RouteTimeout = TimeSpan.FromSeconds(20),
            MaxRouteRetries = 20,
        };

        await using var meshA = MeshMultiplexer.Create(OptionsFor("A"));
        await using var meshB = MeshMultiplexer.Create(OptionsFor("B"));
        await using var meshC = MeshMultiplexer.Create(OptionsFor("C"));
        await using var meshD = MeshMultiplexer.Create(OptionsFor("D"));
        await using var meshE = MeshMultiplexer.Create(OptionsFor("E"));

        meshA.Start(); meshB.Start(); meshC.Start(); meshD.Start(); meshE.Start();

        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);
        meshB.AddNeighbor("C", muxBC_B);
        meshC.AddNeighbor("B", muxBC_C);
        meshB.AddNeighbor("E", muxBE_B);
        meshE.AddNeighbor("B", muxBE_E);
        meshC.AddNeighbor("D", muxCD_C);
        meshD.AddNeighbor("C", muxCD_D);
        meshD.AddNeighbor("E", muxDE_D);
        meshE.AddNeighbor("D", muxDE_E);

        var aReachable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        meshA.NodeReachable += (_, e) => { if (e.NodeId == "E") aReachable.TrySetResult(); };
        await aReachable.Task.WaitAsync(cts.Token);

        // Verify the initial route works (A -> B -> E).
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var r in meshE.AcceptMultiplexersAsync(cts.Token))
            {
                return r;
            }
            throw new InvalidOperationException("Never accepted.");
        }, cts.Token);

        var subA = await meshA.OpenMultiplexerAsync("E", "rpc-1", cts.Token);
        var routed = await acceptTask.WaitAsync(cts.Token);
        var subE = routed.Multiplexer;
        await subE.WaitForReadyAsync(cts.Token);

        var writerA = subA.OpenChannel(new ChannelOptions { ChannelId = "data" });
        var readerE = subE.AcceptChannel("data");
        await Task.WhenAll(writerA.WaitForReadyAsync(cts.Token), readerE.WaitForReadyAsync(cts.Token));
        var ping = new byte[] { 1, 2, 3, 4 };
        await writerA.WriteAsync(ping, cts.Token);
        var pingBuf = new byte[ping.Length];
        int got = 0;
        while (got < ping.Length)
        {
            int n = await readerE.ReadAsync(pingBuf.AsMemory(got), cts.Token);
            if (n == 0) break;
            got += n;
        }
        Assert.Equal(ping, pingBuf);

        // -- Break the B-E link. --
        meshB.RemoveNeighbor("E");
        meshE.RemoveNeighbor("B");
        await muxBE_B.DisposeAsync();
        await muxBE_E.DisposeAsync();

        // Dispose the now-broken endpoint sub-muxes from the original route.
        // Seamless mid-stream reroute is tracked as a separate (skipped) test.
        await writerA.DisposeAsync();
        await readerE.DisposeAsync();
        await subA.DisposeAsync();
        await subE.DisposeAsync();

        // After convergence, opening a brand-new routed mux must succeed via the
        // longer path A -> B -> C -> D -> E.
        var acceptTask2 = Task.Run(async () =>
        {
            await foreach (var r in meshE.AcceptMultiplexersAsync(cts.Token))
            {
                return r;
            }
            throw new InvalidOperationException("Never accepted (post-failover).");
        }, cts.Token);

        await using var subA2 = await meshA.OpenMultiplexerAsync("E", "rpc-2", cts.Token);
        var routed2 = await acceptTask2.WaitAsync(cts.Token);
        await routed2.Multiplexer.WaitForReadyAsync(cts.Token);

        var w2 = subA2.OpenChannel(new ChannelOptions { ChannelId = "data2" });
        var r2 = routed2.Multiplexer.AcceptChannel("data2");
        await Task.WhenAll(w2.WaitForReadyAsync(cts.Token), r2.WaitForReadyAsync(cts.Token));
        var pong = new byte[] { 9, 8, 7, 6, 5 };
        await w2.WriteAsync(pong, cts.Token);
        var pongBuf = new byte[pong.Length];
        got = 0;
        while (got < pong.Length)
        {
            int n = await r2.ReadAsync(pongBuf.AsMemory(got), cts.Token);
            if (n == 0) break;
            got += n;
        }
        Assert.Equal(pong, pongBuf);

        await w2.DisposeAsync();
        await r2.DisposeAsync();
        await routed2.Multiplexer.DisposeAsync();

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
        await muxBC_B.DisposeAsync();
        await muxBC_C.DisposeAsync();
        await muxCD_C.DisposeAsync();
        await muxCD_D.DisposeAsync();
        await muxDE_D.DisposeAsync();
        await muxDE_E.DisposeAsync();
    }

    /// <summary>
    /// Aspirational test for the seamless mid-stream reroute design goal:
    /// when an intermediate edge fails, the existing routed sub-multiplexer
    /// should transparently re-route over the new shortest path without
    /// raising Disconnected on the endpoint sub-muxes, and a payload sent
    /// during/after the outage should arrive exactly once.
    ///
    /// Current gap: when the relay's route channel dies the sub-mux's
    /// underlying transport ends and raises Disconnected at the endpoint;
    /// additionally the underlying StreamMultiplexer replay logic re-delivers
    /// already-consumed bytes after the new transport is established. Both
    /// gaps need to be closed before this test can pass.
    /// </summary>
    [Fact(Skip = "Seamless mid-stream reroute is not yet implemented; tracked as a follow-up.")]
    public Task BreakIntermediateLink_RoutedMuxRerouteSeamlessly()
    {
        return Task.CompletedTask;
    }
}
