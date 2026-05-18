namespace NetConduit.UnitTests;

[Collection("Sequential")]
public sealed class UseCaseTests
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

    [Fact]
    public async Task RequestResponse_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Client sends request, server accepts and opens a response channel
        var request = client.OpenChannel("request");
        var serverReq = await server.AcceptChannelAsync("request", cts.Token);
        var response = server.OpenChannel("response");
        var clientResp = await client.AcceptChannelAsync("response", cts.Token);

        // Client sends request data
        byte[] reqData = "hello server"u8.ToArray();
        await request.WriteAsync(reqData, cts.Token);
        await request.DisposeAsync();

        // Server reads request
        var buf = new byte[256];
        int total = 0;
        int read;
        while ((read = await serverReq.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;
        Assert.Equal(reqData, buf[..total]);

        // Server sends response
        byte[] respData = "hello client"u8.ToArray();
        await response.WriteAsync(respData, cts.Token);
        await response.DisposeAsync();

        // Client reads response
        total = 0;
        while ((read = await clientResp.ReadAsync(buf.AsMemory(total), cts.Token)) > 0)
            total += read;
        Assert.Equal(respData, buf[..total]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PubSub_MultipleChannels_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Server opens multiple "topic" channels
        var topic1 = server.OpenChannel("topic/weather");
        var topic2 = server.OpenChannel("topic/news");

        // Client subscribes by accepting
        var sub1 = await client.AcceptChannelAsync("topic/weather", cts.Token);
        var sub2 = await client.AcceptChannelAsync("topic/news", cts.Token);

        // Server publishes to both topics
        await topic1.WriteAsync("sunny"u8.ToArray(), cts.Token);
        await topic2.WriteAsync("breaking"u8.ToArray(), cts.Token);

        var buf = new byte[256];
        int r1 = await sub1.ReadAsync(buf, cts.Token);
        Assert.Equal("sunny"u8.ToArray(), buf[..r1]);

        int r2 = await sub2.ReadAsync(buf, cts.Token);
        Assert.Equal("breaking"u8.ToArray(), buf[..r2]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task FileTransfer_LargePayload_Correct()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Simulate file transfer: 1MB file
        const int fileSize = 1024 * 1024;
        var fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var writeCh = client.OpenChannel(new ChannelOptions { ChannelId = "file-transfer", SlabSize = 2 * 1024 * 1024 });
        var readCh = await server.AcceptChannelAsync("file-transfer", cts.Token);

        // Send in chunks
        var sendTask = Task.Run(async () =>
        {
            const int chunkSize = 16384;
            for (int offset = 0; offset < fileSize; offset += chunkSize)
            {
                int len = Math.Min(chunkSize, fileSize - offset);
                await writeCh.WriteAsync(fileData.AsMemory(offset, len), cts.Token);
            }
            await writeCh.DisposeAsync();
        }, cts.Token);

        // Receive
        var received = new byte[fileSize];
        int totalRead = 0;
        int read;
        while ((read = await readCh.ReadAsync(received.AsMemory(totalRead), cts.Token)) > 0)
            totalRead += read;

        await sendTask;

        Assert.Equal(fileSize, totalRead);
        Assert.Equal(fileData, received);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Streaming_ContinuousData_NoCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("stream");
        var readCh = await server.AcceptChannelAsync("stream", cts.Token);

        const int messageCount = 100;
        const int messageSize = 512;

        // Send numbered messages
        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                var msg = new byte[messageSize];
                // First 4 bytes = message index
                BitConverter.GetBytes(i).CopyTo(msg, 0);
                Array.Fill(msg, (byte)(i % 256), 4, messageSize - 4);
                await writeCh.WriteAsync(msg, cts.Token);
            }
            await writeCh.DisposeAsync();
        }, cts.Token);

        // Receive and verify
        var allData = new byte[messageCount * messageSize];
        int totalRead = 0;
        int expectedTotal = messageCount * messageSize;
        while (totalRead < expectedTotal)
        {
            int read = await readCh.ReadAsync(allData.AsMemory(totalRead), cts.Token);
            if (read > 0)
                totalRead += read;
            else
                await Task.Yield();
        }

        await sendTask;

        Assert.Equal(messageCount * messageSize, totalRead);

        // Verify each message
        for (int i = 0; i < messageCount; i++)
        {
            int offset = i * messageSize;
            int idx = BitConverter.ToInt32(allData, offset);
            Assert.Equal(i, idx);
            for (int b = 4; b < messageSize; b++)
                Assert.Equal((byte)(i % 256), allData[offset + b]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Bidirectional_FullDuplex_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Both sides open channels to each other
        var clientToServer = client.OpenChannel("c2s");
        var serverToClient = server.OpenChannel("s2c");
        var serverReads = await server.AcceptChannelAsync("c2s", cts.Token);
        var clientReads = await client.AcceptChannelAsync("s2c", cts.Token);

        // Write simultaneously
        byte[] clientMsg = "from client"u8.ToArray();
        byte[] serverMsg = "from server"u8.ToArray();

        await Task.WhenAll(
            clientToServer.WriteAsync(clientMsg, cts.Token).AsTask(),
            serverToClient.WriteAsync(serverMsg, cts.Token).AsTask()
        );

        var buf = new byte[256];
        int r1 = await serverReads.ReadAsync(buf, cts.Token);
        Assert.Equal(clientMsg, buf[..r1]);

        int r2 = await clientReads.ReadAsync(buf, cts.Token);
        Assert.Equal(serverMsg, buf[..r2]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ControlChannel_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Control channel for metadata/commands
        var control = client.OpenChannel("control");
        var controlRead = await server.AcceptChannelAsync("control", cts.Token);

        // Data channels for payloads
        var data1 = client.OpenChannel("data/stream-1");
        var data2 = client.OpenChannel("data/stream-2");
        var readData1 = await server.AcceptChannelAsync("data/stream-1", cts.Token);
        var readData2 = await server.AcceptChannelAsync("data/stream-2", cts.Token);

        // Send control message
        await control.WriteAsync("start"u8.ToArray(), cts.Token);

        // Send data on both streams
        await data1.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await data2.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);

        // Verify control
        var buf = new byte[64];
        int r = await controlRead.ReadAsync(buf, cts.Token);
        Assert.Equal("start"u8.ToArray(), buf[..r]);

        // Verify data
        r = await readData1.ReadAsync(buf, cts.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..r]);
        r = await readData2.ReadAsync(buf, cts.Token);
        Assert.Equal(new byte[] { 4, 5, 6 }, buf[..r]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelPool_OpenCloseReuse_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Accept channels in background
        var acceptTask = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[256];
                while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                count++;
                if (count >= 20) break;
            }
        }, cts.Token);

        // Simulate pooled channel pattern: open, use, close, repeat
        for (int i = 0; i < 20; i++)
        {
            var ch = client.OpenChannel($"pool-{i}");
            await ch.WriteAsync(new byte[] { (byte)i }, cts.Token);
            await ch.DisposeAsync();
        }

        await acceptTask;

        // All 20 channels were successfully opened and closed without error
        Assert.Equal(20, client.Stats.TotalChannelsOpened);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ManySmallMessages_Pattern()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var writeCh = client.OpenChannel("many-small");
        var readCh = await server.AcceptChannelAsync("many-small", cts.Token);

        const int msgCount = 1000;
        const int msgSize = 8;

        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < msgCount; i++)
            {
                var data = BitConverter.GetBytes((long)i);
                await writeCh.WriteAsync(data, cts.Token);
            }
            await writeCh.DisposeAsync();
        }, cts.Token);

        var allData = new byte[msgCount * msgSize];
        int totalRead = 0;
        int expectedTotal = msgCount * msgSize;
        while (totalRead < expectedTotal)
        {
            int read = await readCh.ReadAsync(allData.AsMemory(totalRead), cts.Token);
            if (read > 0)
                totalRead += read;
            else
                await Task.Yield();
        }

        await sendTask;

        Assert.Equal(msgCount * msgSize, totalRead);

        // Verify all messages arrived in order
        for (int i = 0; i < msgCount; i++)
        {
            long val = BitConverter.ToInt64(allData, i * msgSize);
            Assert.Equal(i, val);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ServerPush_MultipleClients_Simulated()
    {
        // Simulates a server pushing data to multiple "clients" (channels)
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int numSubscribers = 10;
        var writers = new IWriteChannel[numSubscribers];
        var readers = new IReadChannel[numSubscribers];

        for (int i = 0; i < numSubscribers; i++)
        {
            writers[i] = server.OpenChannel($"push/{i}");
            readers[i] = await client.AcceptChannelAsync($"push/{i}", cts.Token);
        }

        // Broadcast same message to all
        byte[] broadcast = "broadcast message"u8.ToArray();
        foreach (var w in writers)
            await w.WriteAsync(broadcast, cts.Token);

        // Verify all received
        for (int i = 0; i < numSubscribers; i++)
        {
            var buf = new byte[64];
            int r = await readers[i].ReadAsync(buf, cts.Token);
            Assert.Equal(broadcast, buf[..r]);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
