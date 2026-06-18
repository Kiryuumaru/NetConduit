using System.Buffers.Binary;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for dispose-after-burst-write data truncation:
/// DisposeAsync must not drop the slab before pending frames and FIN drain.
/// </summary>
public sealed class BurstWriteDisposeTruncationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
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
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    [Fact]
    public async Task BurstWrite_1000x256_DisposeAsync_AllMessagesArrive()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream" });
        var reader = server.AcceptChannel("stream");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        // 1,000 writes × 256 bytes = 256 KB — fills the slab window many times over
        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        // Drain all messages from the reader
        var received = new List<long>();
        byte[] buf = new byte[65536];
        int offset = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(offset, buf.Length - offset), cts.Token);
            if (n == 0) break;
            offset += n;
        }

        int msgSize = 256;
        int totalMessages = offset / msgSize;
        for (int i = 0; i < totalMessages; i++)
        {
            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * msgSize, 8));
            received.Add(seq);
        }

        Assert.Equal(1000, received.Count);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BurstWrite_256x256_DisposeAsync_AllMessagesArrive()
    {
        // Exact boundary case: 256 × 256 = 65,536 = 64 KiB
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream256" });
        var reader = server.AcceptChannel("stream256");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        for (int i = 0; i < 256; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        var received = new List<long>();
        byte[] buf = new byte[65536];
        int offset = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(offset, buf.Length - offset), cts.Token);
            if (n == 0) break;
            offset += n;
        }

        int msgSize = 256;
        int totalMessages = offset / msgSize;
        for (int i = 0; i < totalMessages; i++)
        {
            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * msgSize, 8));
            received.Add(seq);
        }

        Assert.Equal(256, received.Count);
        for (int i = 0; i < 256; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BurstWrite_1000x128_DisposeAsync_AllMessagesArrive()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream128" });
        var reader = server.AcceptChannel("stream128");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        int msgSize = 128;
        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[msgSize];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        var received = new List<long>();
        byte[] buf = new byte[200_000];
        int offset = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(offset, buf.Length - offset), cts.Token);
            if (n == 0) break;
            offset += n;
        }

        int totalMessages = offset / msgSize;
        for (int i = 0; i < totalMessages; i++)
        {
            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * msgSize, 8));
            received.Add(seq);
        }

        Assert.Equal(1000, received.Count);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
