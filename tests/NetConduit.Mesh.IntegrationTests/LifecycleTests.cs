namespace NetConduit.Mesh.IntegrationTests;

public class LifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Create_DoesNotStartIO()
    {
        await using var mesh = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        Assert.False(mesh.IsRunning);
        Assert.False(mesh.IsReady);
    }

    [Fact]
    public async Task Start_TwiceThrows()
    {
        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();
        Assert.Throws<InvalidOperationException>(() => meshA.Start());
    }

    [Fact]
    public async Task WaitForReadyAsync_HonorsCancellation()
    {
        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await meshA.WaitForReadyAsync(cts.Token));
    }

    [Fact]
    public async Task PublicMethods_ThrowObjectDisposedAfterDispose()
    {
        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();
        await meshA.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => meshA.Start());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => meshA.WaitForReadyAsync(CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => meshA.OpenMultiplexer("B", "mid"));
        Assert.Throws<ObjectDisposedException>(() => meshA.AcceptMultiplexer("B", "mid"));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        meshA.Start();
        await meshA.DisposeAsync();
        await meshA.DisposeAsync();
    }

    [Fact]
    public async Task DisposeMesh_DoesNotDisposeNeighborMux()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        await meshA.DisposeAsync();
        await meshB.DisposeAsync();

        // Neighbor muxes are still usable for direct application traffic.
        Assert.True(muxAB_A.IsRunning);
        Assert.True(muxAB_B.IsRunning);

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task GoAwayAsync_BeforeStart_NoThrow()
    {
        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await meshA.GoAwayAsync();
        await meshA.DisposeAsync();
    }

    [Fact]
    public async Task OpenMultiplexer_BeforeStart_Throws()
    {
        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        Assert.Throws<InvalidOperationException>(() => meshA.OpenMultiplexer("B", "mid"));
    }

    [Fact]
    public async Task AddNeighbor_BeforeStart_Throws()
    {
        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        Assert.Throws<InvalidOperationException>(
            () => meshA.AddNeighbor("B", null!));
    }
}
