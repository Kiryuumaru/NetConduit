using System.Net;
using System.Net.Sockets;
using System.Text;
using NetConduit.Tcp;

namespace NetConduit.Tcp.IntegrationTests;

public class TcpMultiplexerTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectAndAccept_EstablishesConnection()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        // Act
        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        // Assert
        Assert.True(client.Connected);
        Assert.True(server.Connected);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task OpenChannel_SendsAndReceivesData()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        // Act
        var writeChannel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
        var readChannel = await server.Multiplexer.AcceptChannelAsync("test", cts.Token);

        var testData = "Hello, TCP Multiplexer!"u8.ToArray();
        await writeChannel.WriteAsync(testData, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        var buffer = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        // Assert
        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, buffer);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task MultipleChannels_TransferDataConcurrently()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        const int channelCount = 10;
        const int dataSize = 1024;
        var tasks = new List<Task>();

        // Act - Open multiple channels and send data concurrently
        for (int i = 0; i < channelCount; i++)
        {
            int channelIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var channelId = $"channel-{channelIndex}";
                var writeChannel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                var readChannel = await server.Multiplexer.AcceptChannelAsync(channelId, cts.Token);

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

        // Assert
        Assert.Equal(channelCount, client.Multiplexer.OpenedChannelIds.Count);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task BidirectionalCommunication_BothSidesOpenChannels()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        // Act - Client opens channel to server
        var clientToServerWrite = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "c2s" }, cts.Token);
        var clientToServerRead = await server.Multiplexer.AcceptChannelAsync("c2s", cts.Token);

        // Server opens channel to client
        var serverToClientWrite = await server.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "s2c" }, cts.Token);
        var serverToClientRead = await client.Multiplexer.AcceptChannelAsync("s2c", cts.Token);

        // Send data both ways
        var clientMessage = "Hello from client"u8.ToArray();
        var serverMessage = "Hello from server"u8.ToArray();

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

        // Assert
        Assert.Equal(serverMessage, clientBuffer);
        Assert.Equal(clientMessage, serverBuffer);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task LargeDataTransfer_TransfersCorrectly()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        // Act
        var writeChannel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
        var readChannel = await server.Multiplexer.AcceptChannelAsync("large", cts.Token);

        // 10 MB of data
        const int dataSize = 10 * 1024 * 1024;
        var testData = new byte[dataSize];
        Random.Shared.NextBytes(testData);

        // Write in chunks
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

        // Read
        var buffer = new byte[dataSize];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        await writeTask;

        // Assert
        Assert.Equal(dataSize, totalRead);
        Assert.Equal(testData, buffer);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task FromClient_CreatesMultiplexerFromExistingClient()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverAcceptTask = listener.AcceptTcpClientAsync();
        var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync("127.0.0.1", port);
        var serverTcp = await serverAcceptTask;

        // Act
        await using var client = TcpMultiplexer.FromClient(clientTcp);
        await using var server = TcpMultiplexer.FromClient(serverTcp);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        var writeChannel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
        var readChannel = await server.Multiplexer.AcceptChannelAsync("test", cts.Token);

        var testData = "FromClient test"u8.ToArray();
        await writeChannel.WriteAsync(testData, cts.Token);

        var buffer = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        // Assert
        Assert.Equal(testData, buffer);

        await cts.CancelAsync();
        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectAsync_WithIPEndPoint_Connects()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        // Act
        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync(endpoint);
        await using var server = await serverTask;

        // Assert
        Assert.True(client.Connected);
        Assert.NotNull(client.LocalEndPoint);
        Assert.NotNull(client.RemoteEndPoint);

        listener.Stop();
    }

    [Fact(Timeout = 120000)]
    public async Task GracefulShutdown_ClosesCleanly()
    {
        // Arrange
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var serverTask = TcpMultiplexer.AcceptAsync(listener);
        await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port);
        await using var server = await serverTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
        var clientRunTask = startTasks[0];
        var serverRunTask = startTasks[1];

        // Open a channel
        var writeChannel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
        await server.Multiplexer.AcceptChannelAsync("test", cts.Token);

        // Act - Graceful shutdown
        await client.Multiplexer.GoAwayAsync(cts.Token);

        // Brief delay to allow GOAWAY to be processed
        await Task.Delay(100);

        // Assert - Multiplexer should report shutdown
        Assert.True(client.Multiplexer.IsShuttingDown);

        await cts.CancelAsync();
        listener.Stop();
    }
}
