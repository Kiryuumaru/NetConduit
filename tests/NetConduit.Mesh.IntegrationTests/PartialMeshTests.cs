namespace NetConduit.Mesh.IntegrationTests;

public class PartialMeshTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task UserChannel_CoexistsWithMeshTopologyOnSameMux()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        // Open a user channel BEFORE wiring the mesh.
        var userWriter = muxAB_A.OpenChannel(new ChannelOptions { ChannelId = "app:foo" });
        var userReader = muxAB_B.AcceptChannel("app:foo");

        await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        // Send user traffic while the mesh is running.
        await Task.WhenAll(userWriter.WaitForReadyAsync(cts.Token), userReader.WaitForReadyAsync(cts.Token));
        byte[] payload = "user-data-not-mesh"u8.ToArray();
        await userWriter.WriteAsync(payload, cts.Token);

        var buf = new byte[64];
        int n = await userReader.ReadAsync(buf, cts.Token);
        Assert.Equal(payload, buf[..n]);

        // Mesh did not interfere with the user channel.
        await userWriter.DisposeAsync();
        await userReader.DisposeAsync();

        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }

    [Fact]
    public async Task DisposingMesh_LeavesUserChannelOpen()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (muxAB_A, muxAB_B, _) = await MuxFixture.CreateMuxPairAsync(cts.Token);

        var userWriter = muxAB_A.OpenChannel(new ChannelOptions { ChannelId = "app:bar" });
        var userReader = muxAB_B.AcceptChannel("app:bar");

        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
        meshA.Start();
        meshB.Start();
        meshA.AddNeighbor("B", muxAB_A);
        meshB.AddNeighbor("A", muxAB_B);

        await Task.WhenAll(meshA.WaitForReadyAsync(cts.Token), meshB.WaitForReadyAsync(cts.Token));

        await meshA.DisposeAsync();
        await meshB.DisposeAsync();

        // The user channel still works after mesh disposal.
        await Task.WhenAll(userWriter.WaitForReadyAsync(cts.Token), userReader.WaitForReadyAsync(cts.Token));
        byte[] payload = "still-alive"u8.ToArray();
        await userWriter.WriteAsync(payload, cts.Token);

        var buf = new byte[64];
        int n = await userReader.ReadAsync(buf, cts.Token);
        Assert.Equal(payload, buf[..n]);

        await userWriter.DisposeAsync();
        await userReader.DisposeAsync();
        await muxAB_A.DisposeAsync();
        await muxAB_B.DisposeAsync();
    }
}
