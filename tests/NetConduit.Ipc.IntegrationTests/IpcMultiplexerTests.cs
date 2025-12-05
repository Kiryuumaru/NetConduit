using NetConduit.Ipc;

namespace NetConduit.Ipc.IntegrationTests;

public class IpcMultiplexerTests
{
    [Fact(Timeout = 120000)]
    public async Task IpcMux_SendReceive_Works()
    {
        var endpoint = GetIpcEndpoint("test1");
        CleanupEndpoint(endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = IpcMultiplexer.AcceptAsync(endpoint, cancellationToken: cts.Token);
        var connectTask = IpcMultiplexer.ConnectAsync(endpoint, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        var write = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "ipc" }, cts.Token);
        var read = await server.AcceptChannelAsync("ipc", cts.Token);

        var payload = "hello over ipc"u8.ToArray();
        await write.WriteAsync(payload, cts.Token);
        await write.CloseAsync(cts.Token);

        var buffer = new byte[payload.Length];
        var readBytes = await read.ReadAsync(buffer, cts.Token);
        Assert.Equal(payload.Length, readBytes);
        Assert.Equal(payload, buffer);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);

        CleanupEndpoint(endpoint);
    }

    [Fact(Timeout = 120000)]
    public async Task IpcMux_MultipleChannels_TransferDataConcurrently()
    {
        var endpoint = GetIpcEndpoint("test2");
        CleanupEndpoint(endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = IpcMultiplexer.AcceptAsync(endpoint, cancellationToken: cts.Token);
        var connectTask = IpcMultiplexer.ConnectAsync(endpoint, cancellationToken: cts.Token);

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
                var channelId = $"ipc-channel-{channelIndex}";
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
        Assert.Equal(channelCount, client.OpenedChannelIds.Count);

        cts.Cancel();
        await Task.WhenAll(serverRun, clientRun);

        CleanupEndpoint(endpoint);
    }

    [Fact(Timeout = 120000)]
    public async Task IpcMux_BidirectionalCommunication_BothSidesOpenChannels()
    {
        var endpoint = GetIpcEndpoint("test3");
        CleanupEndpoint(endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = IpcMultiplexer.AcceptAsync(endpoint, cancellationToken: cts.Token);
        var connectTask = IpcMultiplexer.ConnectAsync(endpoint, cancellationToken: cts.Token);

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

        var clientMessage = "Hello from IPC client"u8.ToArray();
        var serverMessage = "Hello from IPC server"u8.ToArray();

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

        CleanupEndpoint(endpoint);
    }

    [Fact(Timeout = 120000)]
    public async Task IpcMux_LargeDataTransfer_TransfersCorrectly()
    {
        var endpoint = GetIpcEndpoint("test4");
        CleanupEndpoint(endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var acceptTask = IpcMultiplexer.AcceptAsync(endpoint, cancellationToken: cts.Token);
        var connectTask = IpcMultiplexer.ConnectAsync(endpoint, cancellationToken: cts.Token);

        await using var server = await acceptTask;
        await using var client = await connectTask;

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRun = startTasks[0];
        var serverRun = startTasks[1];

        var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
        var readChannel = await server.AcceptChannelAsync("large", cts.Token);

        // 5 MB of data
        const int dataSize = 5 * 1024 * 1024;
        var testData = new byte[dataSize];
        Random.Shared.NextBytes(testData);

        var writeTask = Task.Run(async () =>
        {
            const int chunkSize = 64 * 1024;
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

        CleanupEndpoint(endpoint);
    }

    private static string GetIpcEndpoint(string name)
    {
        return OperatingSystem.IsWindows()
            ? $"netconduit-ipc-{name}"
            : Path.Combine(Path.GetTempPath(), $"netconduit-ipc-{name}.sock");
    }

    private static void CleanupEndpoint(string endpoint)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(endpoint))
            File.Delete(endpoint);
    }
}
