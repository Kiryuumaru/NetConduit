namespace NetConduit.UnitTests;

/// <summary>
/// Verifies that <see cref="IWriteChannel.WriteAsync"/>'s cumulative admission
/// bound respects the peer's advertised receive-slab capacity, not just the
/// local outbound slab size. With asymmetric slab configuration (local slab
/// larger than peer cap), the broken implementation admits frames whose
/// cumulative size overflows the peer's read-slab and faults the peer with
/// <c>ErrorCode.ProtocolError</c>.
/// </summary>
public sealed class AsymmetricSlabAdmissionTests
{
    [Fact]
    public async Task WriteAsync_AsymmetricSlab_CumulativeBoundIsPeerCap()
    {
        var duplex = new DuplexMemoryStream();
        const int PeerSlab = 64 * 1024;
        const int LocalSlab = 256 * 1024;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = PeerSlab },
            PingInterval = TimeSpan.Zero,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        int serverErrors = 0;
        server.Error += (_, _) => Interlocked.Increment(ref serverErrors);

        var write = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "asym",
            SlabSize = LocalSlab,
            SendTimeout = TimeSpan.FromMilliseconds(500),
        });
        await write.WaitForReadyAsync();

        // Server intentionally does NOT accept the channel. Without a consumer
        // draining the peer's read-slab, every staged frame stays buffered there.
        // The peer's slab capacity (PeerSlab = 64 KB) is the real cumulative
        // bound on outstanding bytes — not the writer's local 256 KB slab.
        var payload = new byte[PeerSlab - 8];

        // First frame fills exactly one peer-slab worth of unacked bytes.
        await write.WriteAsync(payload);

        // Second frame's cumulative size would overflow the peer's slab.
        // The broken implementation admits it (local SlabSize still has 192 KB
        // free), causing the peer to throw ProtocolError on BufferInSlab and
        // tear down its reader loop. The correct behavior blocks until either
        // an ACK frees space (none will come) or SendTimeout fires.
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await write.WriteAsync(payload);
        });

        // The peer must remain healthy: no ProtocolError, no transport teardown.
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref serverErrors));
        Assert.True(server.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
