namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// Tests that reproduce specific defects identified during the audit. Each test references
/// its finding number from the audit. Tests stay in this file so the historical defects
/// remain documented as executable specifications.
/// </summary>
public class AuditFindingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Audit finding #1: when the remote side initiates GoAway on a routed sub-mux,
    /// <c>StreamMultiplexer</c> raises <c>Disconnected</c> while <c>IsRunning</c> is still
    /// true. The mesh's terminal-disconnect handler gated on <c>IsRunning == false</c> and
    /// therefore never released its opener/acceptor state, leaking <c>ActiveSubMultiplexers</c>.
    /// </summary>
    [Fact]
    public async Task GoAwayInitiatedByRemoteEnd_ReleasesOpenerState()
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

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var r in meshB.AcceptMultiplexersAsync(cts.Token))
            {
                return r;
            }
            throw new InvalidOperationException("Never accepted.");
        }, cts.Token);

        var subA = await meshA.OpenMultiplexerAsync("B", "rpc-1", cts.Token);
        var routed = await acceptTask.WaitAsync(cts.Token);
        await routed.Multiplexer.WaitForReadyAsync(cts.Token);

        Assert.Equal(2L, meshA.Stats.ActiveSubMultiplexers + meshB.Stats.ActiveSubMultiplexers);

        // Remote (B) initiates a graceful shutdown of the routed sub-mux. This causes
        // StreamMultiplexer.Disconnected to fire on A's opener with reason=GoAwayReceived
        // while IsRunning is still true on the A side.
        await routed.Multiplexer.GoAwayAsync(cts.Token);
        await routed.Multiplexer.DisposeAsync();

        // A's opener must observe the terminal disconnect and release its mesh-side state.
        for (int i = 0; i < 250 && meshA.Stats.ActiveSubMultiplexers > 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(0L, meshA.Stats.ActiveSubMultiplexers);
        Assert.Equal(0L, meshB.Stats.ActiveSubMultiplexers);

        await subA.DisposeAsync();
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    /// <summary>
    /// Audit finding #3: <c>RouteForwarder.PipeAsync</c> swallowed every exception thrown
    /// by reads/writes, including legitimate <c>IOException</c> from a dying neighbor mux.
    /// As a result, the mesh's <c>Error</c> event never fired when a relay leg died and
    /// users had no telemetry that the relay terminated abnormally.
    ///
    /// This test sends bytes through a 3-node line, then disposes the next-hop neighbor
    /// mux on the relay side. The relay's outbound write must fail; the mesh on the
    /// relay node must surface that failure via its <c>Error</c> event.
    /// </summary>
    [Fact]
    public async Task RelayLegDies_RaisesErrorOnRelayNode()
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

        var bError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        meshB.Error += (_, e) => bError.TrySetResult(e.Exception);

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

        // Send a small payload to confirm the relay is fully wired.
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        var ack = new byte[3];
        int got = 0;
        while (got < ack.Length)
        {
            int n = await reader.ReadAsync(ack.AsMemory(got), cts.Token);
            if (n == 0) break;
            got += n;
        }
        Assert.Equal(3, got);

        // Kill the relay's forward leg. B's PipeAsync write toward C must fail.
        await muxBC_B.DisposeAsync();

        // Push enough bytes from A so B's PipeAsync goes through a write.
        var payload = new byte[4096];
        try { await writer.WriteAsync(payload, cts.Token); } catch { }

        // The relay node must raise Error within the test timeout.
        var err = await bError.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(err);

        await routed.Multiplexer.DisposeAsync();
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
        await muxBC_C.DisposeAsync();
    }

    /// <summary>
    /// Audit finding #4: <c>OpenMultiplexer</c> incremented <c>RoutesOpened</c> immediately
    /// after constructing the sub-mux, before any I/O occurred. A failed open
    /// (no route within the timeout) additionally incremented <c>RoutesFailed</c>, so a
    /// single failed open inflated both counters. Any "success rate" calculation
    /// based on opened/(opened+failed) was wrong.
    /// </summary>
    [Fact]
    public async Task OpenToUnreachableNode_OnlyIncrementsRoutesFailed()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions
        {
            NodeId = "A",
            RouteTimeout = TimeSpan.FromMilliseconds(500),
            MaxRouteRetries = 0,
        });
        meshA.Start();

        long openedBefore = meshA.Stats.RoutesOpened;
        long failedBefore = meshA.Stats.RoutesFailed;

        await Assert.ThrowsAnyAsync<Exception>(
            () => meshA.OpenMultiplexerAsync("Z", "rpc-fail", cts.Token));

        long openedDelta = meshA.Stats.RoutesOpened - openedBefore;
        long failedDelta = meshA.Stats.RoutesFailed - failedBefore;

        Assert.True(failedDelta >= 1, $"Expected RoutesFailed to increment, got delta {failedDelta}.");
        Assert.Equal(0L, openedDelta);
    }
}
