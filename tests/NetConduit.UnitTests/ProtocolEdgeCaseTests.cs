namespace NetConduit.UnitTests;

/// <summary>
/// Tests for protocol edge cases and boundary conditions:
/// - Channel ID edge cases (long names, special characters, unicode, similar prefixes)
/// - Data size edge cases (single byte, large payload, many small writes)
/// - Accept before Open timing
/// - Concurrent named accepts
/// </summary>
[Collection("Sequential")]
public sealed class ProtocolEdgeCaseTests
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

    #region Channel ID Edge Cases

    [Fact]
    public async Task ChannelId_LongName_WorksCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var longId = new string('x', 500);
        var writer = client.OpenChannel(longId);
        var reader = await server.AcceptChannelAsync(longId, cts.Token);

        await writer.WriteAsync(new byte[] { 42 }, cts.Token);
        await writer.DisposeAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(42, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelId_SpecialCharacters_WorksCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var specialId = "ch/test:with.special-chars_and spaces!@#$%";
        var writer = client.OpenChannel(specialId);
        var reader = await server.AcceptChannelAsync(specialId, cts.Token);

        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await writer.DisposeAsync();

        var buf = new byte[16];
        int total = 0;
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;
        Assert.Equal(3, total);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelId_Unicode_WorksCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var unicodeId = "频道_チャンネル_канал";
        var writer = client.OpenChannel(unicodeId);
        var reader = await server.AcceptChannelAsync(unicodeId, cts.Token);

        await writer.WriteAsync(new byte[] { 99 }, cts.Token);
        await writer.DisposeAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(99, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelId_SimilarPrefixes_NoConfusion()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Channels with similar prefixes
        var w1 = client.OpenChannel("channel");
        var r1 = await server.AcceptChannelAsync("channel", cts.Token);

        var w2 = client.OpenChannel("channel_1");
        var r2 = await server.AcceptChannelAsync("channel_1", cts.Token);

        var w3 = client.OpenChannel("channel_10");
        var r3 = await server.AcceptChannelAsync("channel_10", cts.Token);

        // Write unique data to each
        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        await w1.DisposeAsync();
        await w2.WriteAsync(new byte[] { 2 }, cts.Token);
        await w2.DisposeAsync();
        await w3.WriteAsync(new byte[] { 3 }, cts.Token);
        await w3.DisposeAsync();

        // Each reader gets its own data
        var buf1 = new byte[1];
        Assert.Equal(1, await r1.ReadAsync(buf1, cts.Token));
        Assert.Equal(1, buf1[0]);

        var buf2 = new byte[1];
        Assert.Equal(1, await r2.ReadAsync(buf2, cts.Token));
        Assert.Equal(2, buf2[0]);

        var buf3 = new byte[1];
        Assert.Equal(1, await r3.ReadAsync(buf3, cts.Token));
        Assert.Equal(3, buf3[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Data Size Edge Cases

    [Fact]
    public async Task SingleByteWrite_ReceivedCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writer = client.OpenChannel("tiny");
        var reader = await server.AcceptChannelAsync("tiny", cts.Token);

        await writer.WriteAsync(new byte[] { 0xFF }, cts.Token);
        await writer.DisposeAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xFF, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task LargePayload_256KB_ReceivedCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var writer = client.OpenChannel("large");
        var reader = await server.AcceptChannelAsync("large", cts.Token);

        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.DisposeAsync();

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
            ms.Write(buf, 0, read);

        Assert.Equal(data.Length, ms.Length);
        Assert.True(data.AsSpan().SequenceEqual(ms.ToArray()));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ManySmallWrites_AllReceivedInOrder()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var writer = client.OpenChannel("many-small");
        var reader = await server.AcceptChannelAsync("many-small", cts.Token);

        const int count = 500;
        for (int i = 0; i < count; i++)
            await writer.WriteAsync(new byte[] { (byte)(i % 256) }, cts.Token);
        await writer.DisposeAsync();

        var received = new MemoryStream();
        var buf = new byte[1024];
        while (received.Length < count)
        {
            int read = await reader.ReadAsync(buf, cts.Token);
            if (read > 0)
                received.Write(buf, 0, read);
            else
                await Task.Yield();
        }

        var result = received.ToArray();
        Assert.Equal(count, result.Length);
        for (int i = 0; i < count; i++)
            Assert.Equal((byte)(i % 256), result[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Timing Edge Cases

    [Fact]
    public async Task AcceptBeforeOpen_NamedAccept_Works()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start accepting before opening
        var acceptTask = server.AcceptChannelAsync("late-open", cts.Token);

        await Task.Delay(100, cts.Token);

        // Now open the channel
        var writer = client.OpenChannel("late-open");
        await writer.WriteAsync(new byte[] { 42 }, cts.Token);

        var reader = await acceptTask;
        var buf = new byte[1];
        Assert.Equal(1, await reader.ReadAsync(buf, cts.Token));
        Assert.Equal(42, buf[0]);

        await writer.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentNamedAccept_AllChannelsMatchCorrectly()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start multiple accepts concurrently
        var accept1 = server.AcceptChannelAsync("ch-b", cts.Token);
        var accept2 = server.AcceptChannelAsync("ch-a", cts.Token);
        var accept3 = server.AcceptChannelAsync("ch-c", cts.Token);

        // Open in different order
        var w1 = client.OpenChannel("ch-a");
        var w2 = client.OpenChannel("ch-b");
        var w3 = client.OpenChannel("ch-c");

        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        await w2.WriteAsync(new byte[] { 2 }, cts.Token);
        await w3.WriteAsync(new byte[] { 3 }, cts.Token);

        var r1 = await accept2; // ch-a
        var r2 = await accept1; // ch-b
        var r3 = await accept3; // ch-c

        var buf = new byte[1];
        Assert.Equal(1, await r1.ReadAsync(buf, cts.Token));
        Assert.Equal(1, buf[0]);

        Assert.Equal(1, await r2.ReadAsync(buf, cts.Token));
        Assert.Equal(2, buf[0]);

        Assert.Equal(1, await r3.ReadAsync(buf, cts.Token));
        Assert.Equal(3, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelsAsync_ContinuesAccepting_AcrossMultipleOpens()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptedIds = new List<string>();
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                acceptedIds.Add(ch.ChannelId);
                if (acceptedIds.Count >= 5) break;
            }
        });

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(50, cts.Token);
            client.OpenChannel($"seq-{i}");
        }

        await acceptTask.WaitAsync(cts.Token);
        Assert.Equal(5, acceptedIds.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal($"seq-{i}", acceptedIds[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BothSidesOpenChannels_Simultaneously_NoConflict()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Both sides open channels simultaneously
        var clientWrite = client.OpenChannel("from-client");
        var serverWrite = server.OpenChannel("from-server");

        var clientRead = await client.AcceptChannelAsync("from-server", cts.Token);
        var serverRead = await server.AcceptChannelAsync("from-client", cts.Token);

        await clientWrite.WriteAsync(new byte[] { 1 }, cts.Token);
        await serverWrite.WriteAsync(new byte[] { 2 }, cts.Token);

        var buf = new byte[1];
        Assert.Equal(1, await serverRead.ReadAsync(buf, cts.Token));
        Assert.Equal(1, buf[0]);
        Assert.Equal(1, await clientRead.ReadAsync(buf, cts.Token));
        Assert.Equal(2, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}
