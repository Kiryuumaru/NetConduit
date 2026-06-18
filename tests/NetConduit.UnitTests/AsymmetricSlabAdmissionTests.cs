namespace NetConduit.UnitTests;

/// <summary>
/// Verifies that <see cref="IWriteChannel.WriteAsync"/> bounds cumulative
/// outstanding bytes against the peer's advertised receive-slab capacity, not
/// just the local outbound slab size. Without this clamp, a writer configured
/// with <c>ChannelOptions.SlabSize</c> larger than the peer's
/// <c>DefaultChannelOptions.SlabSize</c> overflows the peer's
/// <c>ReadChannel</c> on the second frame, faulting the peer with
/// <c>ErrorCode.ProtocolError</c> and triggering an infinite reconnect-replay
/// loop until <c>MaxAutoReconnectAttempts</c> is exhausted.
/// </summary>
public sealed class AsymmetricSlabAdmissionTests
{
    [Fact]
    public async Task WriteAsync_AsymmetricSlab_DoesNotOverflowPeerSlab()
    {
        var duplex = new DuplexMemoryStream();
        const int PeerSlab = 64 * 1024;
        const int LocalSlab = 4 * 1024 * 1024;

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

        Exception? serverError = null;
        server.Error += (_, e) => serverError ??= e.Exception;

        var write = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "asym",
            SlabSize = LocalSlab,
            SendTimeout = TimeSpan.FromMilliseconds(500),
        });
        await write.WaitForReadyAsync();

        // Server does NOT accept the channel — no consumer, no consumer-position
        // ACKs flow back. The writer must NOT stage cumulative payload bytes
        // exceeding the peer's slab cap (PeerSlab = 64 KB). Under the broken
        // implementation, all frames stage into the writer's 4 MB local slab
        // and ship on the wire; the peer's BufferInSlab then throws
        // ErrorCode.ProtocolError on the second frame.
        var payload = new byte[PeerSlab - 8];

        for (int i = 0; i < 8; i++)
        {
            try
            {
                await write.WriteAsync(payload);
            }
            catch (TimeoutException) { break; }
            catch (ChannelClosedException) { break; }
        }

        // Give any in-flight frames time to reach the peer.
        await Task.Delay(300);

        Assert.Null(serverError);
        Assert.True(server.IsConnected, "Server transport must remain connected.");
        Assert.False(write.CloseReason is ChannelCloseReason.TransportFailed,
            "Write channel must not have observed a transport-level fault.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteAsync_AsymmetricSlab_WithConsumer_DrainsCleanly()
    {
        // Lock-in: when the peer accepts and drains, the asymmetric admission
        // bound still allows steady-state throughput — consumer-position ACKs
        // free outstanding bytes and the writer keeps streaming.
        var duplex = new DuplexMemoryStream();
        const int PeerSlab = 64 * 1024;
        const int LocalSlab = 1 * 1024 * 1024;

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

        var write = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "asym-drain",
            SlabSize = LocalSlab,
            SendTimeout = TimeSpan.FromSeconds(10),
        });
        var read = server.AcceptChannel("asym-drain");
        await Task.WhenAll(write.WaitForReadyAsync(), read.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        const int FrameCount = 16;
        long bytesRead = 0;
        var drainer = Task.Run(async () =>
        {
            var buf = new byte[PeerSlab];
            while (bytesRead < (long)FrameCount * (PeerSlab - 8))
            {
                int n = await read.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                Interlocked.Add(ref bytesRead, n);
            }
        });

        var payload = new byte[PeerSlab - 8];
        for (int i = 0; i < FrameCount; i++)
            await write.WriteAsync(payload, cts.Token);

        await drainer.WaitAsync(cts.Token);
        Assert.Equal((long)FrameCount * (PeerSlab - 8), Volatile.Read(ref bytesRead));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
