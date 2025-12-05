using NetConduit.Udp;

namespace NetConduit.Udp.IntegrationTests;

public class UdpMultiplexerTests
{
    [Fact(Timeout = 120000)]
    public async Task UdpMux_SendReceive_Works()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = UdpMultiplexer.AcceptAsync(port, cancellationToken: cts.Token);
        var connectTask = UdpMultiplexer.ConnectAsync("127.0.0.1", port, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        // open channel from client to server
        var write = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "udp-test" }, cts.Token);
        var read = await server.AcceptChannelAsync("udp-test", cts.Token);

        var payload = "hello over udp"u8.ToArray();
        await write.WriteAsync(payload, cts.Token);
        await write.CloseAsync(cts.Token);

        var buffer = new byte[payload.Length];
        var readBytes = await read.ReadAsync(buffer, cts.Token);
        Assert.Equal(payload.Length, readBytes);
        Assert.Equal(payload, buffer);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);
    }

    [Fact(Timeout = 120000)]
    public async Task UdpMux_MultipleChannels_TransferDataConcurrently()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = UdpMultiplexer.AcceptAsync(port, cancellationToken: cts.Token);
        var connectTask = UdpMultiplexer.ConnectAsync("127.0.0.1", port, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        const int channelCount = 5;
        const int dataSize = 512;
        var tasks = new List<Task>();

        for (int i = 0; i < channelCount; i++)
        {
            int channelIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var channelId = $"udp-channel-{channelIndex}";
                var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                var readChannel = await server.AcceptChannelAsync(channelId, cts.Token);

                var testData = new byte[dataSize];
                Random.Shared.NextBytes(testData);

                await writeChannel.WriteAsync(testData, cts.Token);
                await writeChannel.CloseAsync(cts.Token);

                var buffer = new byte[dataSize];
                int totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }

                Assert.Equal(testData.Length, totalRead);
                Assert.Equal(testData, buffer);
            }, cts.Token));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(channelCount, client.Multiplexer.OpenedChannelIds.Count);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);
    }

    [Fact(Timeout = 120000)]
    public async Task UdpMux_BidirectionalCommunication_BothSidesOpenChannels()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = UdpMultiplexer.AcceptAsync(port, cancellationToken: cts.Token);
        var connectTask = UdpMultiplexer.ConnectAsync("127.0.0.1", port, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        // Client opens channel to server
        var clientToServerWrite = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "c2s" }, cts.Token);
        var clientToServerRead = await server.AcceptChannelAsync("c2s", cts.Token);

        // Server opens channel to client
        var serverToClientWrite = await server.OpenChannelAsync(new ChannelOptions { ChannelId = "s2c" }, cts.Token);
        var serverToClientRead = await client.AcceptChannelAsync("s2c", cts.Token);

        var clientMessage = "Hello from UDP client"u8.ToArray();
        var serverMessage = "Hello from UDP server"u8.ToArray();

        await clientToServerWrite.WriteAsync(clientMessage, cts.Token);
        await serverToClientWrite.WriteAsync(serverMessage, cts.Token);

        var clientBuffer = new byte[serverMessage.Length];
        var serverBuffer = new byte[clientMessage.Length];

        int clientRead = 0, serverRead = 0;
        while (clientRead < clientBuffer.Length)
        {
            int read = await serverToClientRead.ReadAsync(clientBuffer.AsMemory(clientRead), cts.Token);
            if (read == 0) break;
            clientRead += read;
        }
        while (serverRead < serverBuffer.Length)
        {
            int read = await clientToServerRead.ReadAsync(serverBuffer.AsMemory(serverRead), cts.Token);
            if (read == 0) break;
            serverRead += read;
        }

        Assert.Equal(serverMessage, clientBuffer);
        Assert.Equal(clientMessage, serverBuffer);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);
    }

    [Fact(Timeout = 120000)]
    public async Task UdpMux_LargeDataTransfer_TransfersCorrectly()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = UdpMultiplexer.AcceptAsync(port, cancellationToken: cts.Token);
        var connectTask = UdpMultiplexer.ConnectAsync("127.0.0.1", port, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
        var readChannel = await server.AcceptChannelAsync("large", cts.Token);

        // 1 MB of data (smaller than TCP test due to UDP characteristics)
        const int dataSize = 1 * 1024 * 1024;
        var testData = new byte[dataSize];
        Random.Shared.NextBytes(testData);

        var writeTask = Task.Run(async () =>
        {
            const int chunkSize = 32 * 1024;
            for (int offset = 0; offset < dataSize; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, dataSize - offset);
                await writeChannel.WriteAsync(testData.AsMemory(offset, length), cts.Token);
            }
            await writeChannel.CloseAsync(cts.Token);
        }, cts.Token);

        var buffer = new byte[dataSize];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        await writeTask;

        Assert.Equal(dataSize, totalRead);
        Assert.Equal(testData, buffer);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
