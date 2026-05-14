using System.Security.Cryptography;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

[Collection("Sequential")]
public sealed class NestedMuxTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        return (client, server);
    }

    private static async Task<(IWriteChannel ClientWrite, IReadChannel ServerRead,
                                IWriteChannel ServerWrite, IReadChannel ClientRead)>
        CreateBidirectionalChannelPairAsync(
            IStreamMultiplexer client, IStreamMultiplexer server,
            string fwdId, string revId, CancellationToken ct)
    {
        var clientRead = client.AcceptChannel(revId);
        var serverRead = server.AcceptChannel(fwdId);
        var clientWrite = client.OpenChannel(fwdId);
        var serverWrite = server.OpenChannel(revId);

        await Task.WhenAll(
            clientRead.WaitForReadyAsync(ct),
            serverRead.WaitForReadyAsync(ct),
            clientWrite.WaitForReadyAsync(ct),
            serverWrite.WaitForReadyAsync(ct));

        return (clientWrite, serverRead, serverWrite, clientRead);
    }

    private static (StreamMultiplexer Inner1, StreamMultiplexer Inner2) CreateMuxOverChannels(
        IWriteChannel write1, IReadChannel read1,
        IWriteChannel write2, IReadChannel read2)
    {
        var transport1 = new ChannelStreamPair(write1, read1);
        var transport2 = new ChannelStreamPair(write2, read2);

        var inner1 = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(transport1),
        });
        var inner2 = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(transport2),
        });
        return (inner1, inner2);
    }

    private static async Task<(StreamMultiplexer InnerClient, StreamMultiplexer InnerServer)>
        CreateNestedMuxAsync(
            IStreamMultiplexer outerClient, IStreamMultiplexer outerServer,
            string fwdId, string revId, CancellationToken ct)
    {
        var (cWrite, sRead, sWrite, cRead) = await CreateBidirectionalChannelPairAsync(
            outerClient, outerServer, fwdId, revId, ct);

        var (innerClient, innerServer) = CreateMuxOverChannels(cWrite, cRead, sWrite, sRead);
        innerClient.Start();
        innerServer.Start();
        await Task.WhenAll(innerClient.WaitForReadyAsync(ct), innerServer.WaitForReadyAsync(ct));
        return (innerClient, innerServer);
    }

    [Fact]
    public async Task NestedMux_SingleLevel_DataTransfersCorrectly()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (innerClient, innerServer) = await CreateNestedMuxAsync(
            outerClient, outerServer, "fwd", "rev", cts.Token);

        var writer = await innerClient.OpenChannelAsync("data", cts.Token);
        var reader = await innerServer.AcceptChannelAsync("data", cts.Token);

        var testData = new byte[4096];
        Random.Shared.NextBytes(testData);
        var expectedHash = SHA256.HashData(testData);

        await writer.WriteAsync(testData, cts.Token);
        await writer.DisposeAsync();

        var received = new byte[4096];
        int total = 0;
        while (total < testData.Length)
        {
            var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(testData.Length, total);
        Assert.Equal(expectedHash, SHA256.HashData(received.AsSpan(0, total)));

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_TwoLevels_DataTransfersCorrectly()
    {
        var (l1Client, l1Server) = CreatePair();
        l1Client.Start();
        l1Server.Start();
        await Task.WhenAll(l1Client.WaitForReadyAsync(), l1Server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (l2Client, l2Server) = await CreateNestedMuxAsync(
            l1Client, l1Server, "l1_fwd", "l1_rev", cts.Token);

        var (l3Client, l3Server) = await CreateNestedMuxAsync(
            l2Client, l2Server, "l2_fwd", "l2_rev", cts.Token);

        var writer = await l3Client.OpenChannelAsync("deep", cts.Token);
        var reader = await l3Server.AcceptChannelAsync("deep", cts.Token);

        var testData = new byte[8192];
        Random.Shared.NextBytes(testData);
        var expectedHash = SHA256.HashData(testData);

        await writer.WriteAsync(testData, cts.Token);
        await writer.DisposeAsync();

        var received = new byte[8192];
        int total = 0;
        while (total < testData.Length)
        {
            var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(testData.Length, total);
        Assert.Equal(expectedHash, SHA256.HashData(received.AsSpan(0, total)));

        await l3Client.DisposeAsync();
        await l3Server.DisposeAsync();
        await l2Client.DisposeAsync();
        await l2Server.DisposeAsync();
        await l1Client.DisposeAsync();
        await l1Server.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_ThreeLevels_DataTransfersCorrectly()
    {
        var (l1Client, l1Server) = CreatePair();
        l1Client.Start();
        l1Server.Start();
        await Task.WhenAll(l1Client.WaitForReadyAsync(), l1Server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (l2Client, l2Server) = await CreateNestedMuxAsync(
            l1Client, l1Server, "l1_fwd", "l1_rev", cts.Token);

        var (l3Client, l3Server) = await CreateNestedMuxAsync(
            l2Client, l2Server, "l2_fwd", "l2_rev", cts.Token);

        var (l4Client, l4Server) = await CreateNestedMuxAsync(
            l3Client, l3Server, "l3_fwd", "l3_rev", cts.Token);

        var writer = await l4Client.OpenChannelAsync("deepest", cts.Token);
        var reader = await l4Server.AcceptChannelAsync("deepest", cts.Token);

        var testData = new byte[4096];
        Random.Shared.NextBytes(testData);
        var expectedHash = SHA256.HashData(testData);

        await writer.WriteAsync(testData, cts.Token);
        await writer.DisposeAsync();

        var received = new byte[4096];
        int total = 0;
        while (total < testData.Length)
        {
            var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(testData.Length, total);
        Assert.Equal(expectedHash, SHA256.HashData(received.AsSpan(0, total)));

        await l4Client.DisposeAsync();
        await l4Server.DisposeAsync();
        await l3Client.DisposeAsync();
        await l3Server.DisposeAsync();
        await l2Client.DisposeAsync();
        await l2Server.DisposeAsync();
        await l1Client.DisposeAsync();
        await l1Server.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_MultipleChannelsOnInnerMux_AllDataCorrect()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (innerClient, innerServer) = await CreateNestedMuxAsync(
            outerClient, outerServer, "fwd", "rev", cts.Token);

        const int channelCount = 10;
        const int dataSize = 1024;
        var sentHashes = new byte[channelCount][];
        var receivedHashes = new byte[channelCount][];

        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in innerServer.AcceptChannelsAsync(cts.Token))
            {
                var idx = int.Parse(ch.ChannelId.Replace("ch-", ""));
                var received = new byte[dataSize];
                int total = 0;
                while (total < dataSize)
                {
                    var read = await ch.ReadAsync(received.AsMemory(total), cts.Token);
                    if (read == 0) break;
                    total += read;
                }
                receivedHashes[idx] = SHA256.HashData(received.AsSpan(0, total));
                count++;
                if (count >= channelCount) break;
            }
        });

        for (int i = 0; i < channelCount; i++)
        {
            var data = new byte[dataSize];
            Random.Shared.NextBytes(data);
            sentHashes[i] = SHA256.HashData(data);

            var writer = await innerClient.OpenChannelAsync($"ch-{i}", cts.Token);
            await writer.WriteAsync(data, cts.Token);
            await writer.DisposeAsync();
        }

        await acceptTask.WaitAsync(cts.Token);

        for (int i = 0; i < channelCount; i++)
            Assert.Equal(sentHashes[i], receivedHashes[i]);

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_MultipleInnerMuxes_OnSameOuterMux()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        const int innerMuxCount = 3;
        var innerPairs = new (StreamMultiplexer Client, StreamMultiplexer Server)[innerMuxCount];

        for (int i = 0; i < innerMuxCount; i++)
        {
            innerPairs[i] = await CreateNestedMuxAsync(
                outerClient, outerServer, $"fwd-{i}", $"rev-{i}", cts.Token);
        }

        var results = new byte[innerMuxCount][];
        var expected = new byte[innerMuxCount][];

        for (int i = 0; i < innerMuxCount; i++)
        {
            var data = new byte[512];
            Random.Shared.NextBytes(data);
            expected[i] = SHA256.HashData(data);

            var writer = await innerPairs[i].Client.OpenChannelAsync("data", cts.Token);
            var reader = await innerPairs[i].Server.AcceptChannelAsync("data", cts.Token);

            await writer.WriteAsync(data, cts.Token);
            await writer.DisposeAsync();

            var received = new byte[512];
            int total = 0;
            while (total < data.Length)
            {
                var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
                if (read == 0) break;
                total += read;
            }
            results[i] = SHA256.HashData(received.AsSpan(0, total));
        }

        for (int i = 0; i < innerMuxCount; i++)
            Assert.Equal(expected[i], results[i]);

        for (int i = innerMuxCount - 1; i >= 0; i--)
        {
            await innerPairs[i].Client.DisposeAsync();
            await innerPairs[i].Server.DisposeAsync();
        }

        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_BidirectionalOnInner_BothDirectionsWork()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (innerClient, innerServer) = await CreateNestedMuxAsync(
            outerClient, outerServer, "fwd", "rev", cts.Token);

        // Client → Server direction
        var c2sWriter = await innerClient.OpenChannelAsync("c2s", cts.Token);
        var c2sReader = await innerServer.AcceptChannelAsync("c2s", cts.Token);

        // Server → Client direction
        var s2cRead = innerClient.AcceptChannel("s2c");
        var s2cWriter = await innerServer.OpenChannelAsync("s2c", cts.Token);
        await s2cRead.WaitForReadyAsync(cts.Token);

        var clientData = new byte[256];
        var serverData = new byte[256];
        Random.Shared.NextBytes(clientData);
        Random.Shared.NextBytes(serverData);

        await c2sWriter.WriteAsync(clientData, cts.Token);
        await c2sWriter.DisposeAsync();

        await s2cWriter.WriteAsync(serverData, cts.Token);
        await s2cWriter.DisposeAsync();

        var c2sReceived = new byte[256];
        int c2sTotal = 0;
        while (c2sTotal < 256)
        {
            var read = await c2sReader.ReadAsync(c2sReceived.AsMemory(c2sTotal), cts.Token);
            if (read == 0) break;
            c2sTotal += read;
        }

        var s2cReceived = new byte[256];
        int s2cTotal = 0;
        while (s2cTotal < 256)
        {
            var read = await s2cRead.ReadAsync(s2cReceived.AsMemory(s2cTotal), cts.Token);
            if (read == 0) break;
            s2cTotal += read;
        }

        Assert.Equal(clientData, c2sReceived);
        Assert.Equal(serverData, s2cReceived);

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_DuplexStreamTransitAsTransport_Works()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Use DuplexStreamTransit to create the bidirectional transport
        var (cWrite, sRead, sWrite, cRead) = await CreateBidirectionalChannelPairAsync(
            outerClient, outerServer, "fwd", "rev", cts.Token);

        var clientDuplex = new DuplexStreamTransit(cWrite, cRead);
        var serverDuplex = new DuplexStreamTransit(sWrite, sRead);

        var innerClient = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(
                new DuplexStreamPair(clientDuplex)),
        });
        var innerServer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(
                new DuplexStreamPair(serverDuplex)),
        });

        innerClient.Start();
        innerServer.Start();
        await Task.WhenAll(innerClient.WaitForReadyAsync(cts.Token),
                           innerServer.WaitForReadyAsync(cts.Token));

        var writer = await innerClient.OpenChannelAsync("test", cts.Token);
        var reader = await innerServer.AcceptChannelAsync("test", cts.Token);

        var testData = new byte[2048];
        Random.Shared.NextBytes(testData);

        await writer.WriteAsync(testData, cts.Token);
        await writer.DisposeAsync();

        var received = new byte[2048];
        int total = 0;
        while (total < testData.Length)
        {
            var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(testData, received);

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_DisposingOuter_InnerOperationsFail()
    {
        var (outerClient, outerServer) = CreatePair();
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (innerClient, innerServer) = await CreateNestedMuxAsync(
            outerClient, outerServer, "fwd", "rev", cts.Token);

        var writer = await innerClient.OpenChannelAsync("data", cts.Token);
        var reader = await innerServer.AcceptChannelAsync("data", cts.Token);

        // Verify working before dispose
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        var buf = new byte[3];
        int read = 0;
        while (read < 3)
        {
            var r = await reader.ReadAsync(buf.AsMemory(read), cts.Token);
            if (r == 0) break;
            read += r;
        }
        Assert.Equal(3, read);

        // Dispose outer — inner transport is severed
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();

        // Inner mux reads that depend on the outer transport
        // will block until cancellation since the underlying
        // streams are gone. Verify it does not return data.
        using var shortCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var postDisposeBuf = new byte[10];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await reader.ReadAsync(postDisposeBuf, shortCts.Token);
        });

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
    }

    private sealed class ChannelStreamPair(IWriteChannel writeChannel, IReadChannel readChannel) : IStreamPair
    {
        public Stream ReadStream => readChannel.AsStream();
        public Stream WriteStream => writeChannel.AsStream();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DuplexStreamPair(Stream duplexStream) : IStreamPair
    {
        public Stream ReadStream => duplexStream;
        public Stream WriteStream => duplexStream;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
