using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetConduit.WebSocket;

namespace NetConduit.WebSocket.IntegrationTests;

public class WebSocketMultiplexerTests
{
    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(IHost host, int port, Channel<System.Net.WebSockets.WebSocket> webSocketChannel)> CreateServerAsync()
    {
        var port = GetAvailablePort();
        var webSocketChannel = System.Threading.Channels.Channel.CreateUnbounded<System.Net.WebSockets.WebSocket>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://localhost:{port}");
                webBuilder.ConfigureLogging(logging => logging.ClearProviders());
                webBuilder.Configure(app =>
                {
                    app.UseWebSockets();
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Path == "/ws" && context.WebSockets.IsWebSocketRequest)
                        {
                            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                            await webSocketChannel.Writer.WriteAsync(webSocket);

                            // Keep the connection alive until the WebSocket is closed
                            // Don't read here - let the multiplexer handle it
                            var tcs = new TaskCompletionSource();
                            _ = Task.Run(async () =>
                            {
                                while (webSocket.State == WebSocketState.Open)
                                {
                                    await Task.Delay(100);
                                }
                                tcs.SetResult();
                            });
                            await tcs.Task;
                        }
                        else
                        {
                            await next();
                        }
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return (host, port, webSocketChannel);
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectAsync_EstablishesConnection()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            // Act
            await using var client = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");

            // Assert
            Assert.Equal(WebSocketState.Open, client.State);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectAsync_WithUri_EstablishesConnection()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            // Act
            await using var client = await WebSocketMultiplexer.ConnectAsync(new Uri($"ws://localhost:{port}/ws"));

            // Assert
            Assert.Equal(WebSocketState.Open, client.State);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task Accept_CreatesMultiplexerFromWebSocket()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Connect client which triggers server to accept
            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);

            // Act
            await using var serverConnection = WebSocketMultiplexer.Accept(serverWebSocket);

            // Assert
            Assert.NotNull(serverConnection);
            Assert.Equal(WebSocketState.Open, serverConnection.State);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task OpenChannel_SendsAndReceivesData()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = WebSocketMultiplexer.Accept(serverWebSocket);

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));
            var clientRunTask = startTasks[0];
            var serverRunTask = startTasks[1];

            // Act
            var writeChannel = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
            var readChannel = await serverConnection.AcceptChannelAsync("test", cts.Token);

            var testData = "Hello, WebSocket Multiplexer!"u8.ToArray();
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
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task MultipleChannels_TransferDataConcurrently()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = WebSocketMultiplexer.Accept(serverWebSocket);

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));
            var clientRunTask = startTasks[0];
            var serverRunTask = startTasks[1];

            const int channelCount = 5;
            const int dataSize = 1024;
            var tasks = new List<Task>();

            // Act - Open multiple channels and send data concurrently
            for (int i = 0; i < channelCount; i++)
            {
                int channelIndex = i;
                tasks.Add(Task.Run(async () =>
                {
                    var channelId = $"channel-{channelIndex}";
                    var writeChannel = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                    var readChannel = await serverConnection.AcceptChannelAsync(channelId, cts.Token);

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
            Assert.Equal(channelCount, clientConnection.OpenedChannelIds.Count);

            await cts.CancelAsync();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task BidirectionalCommunication_BothSidesOpenChannels()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = WebSocketMultiplexer.Accept(serverWebSocket);

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));
            var clientRunTask = startTasks[0];
            var serverRunTask = startTasks[1];

            // Act - Client opens channel to server
            var clientToServerWrite = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "c2s" }, cts.Token);
            var clientToServerRead = await serverConnection.AcceptChannelAsync("c2s", cts.Token);

            // Server opens channel to client
            var serverToClientWrite = await serverConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "s2c" }, cts.Token);
            var serverToClientRead = await clientConnection.AcceptChannelAsync("s2c", cts.Token);

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
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task LargeDataTransfer_TransfersCorrectly()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = WebSocketMultiplexer.Accept(serverWebSocket);

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));
            var clientRunTask = startTasks[0];
            var serverRunTask = startTasks[1];

            // Act
            var writeChannel = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
            var readChannel = await serverConnection.AcceptChannelAsync("large", cts.Token);

            // 1 MB of data
            const int dataSize = 1024 * 1024;
            var testData = new byte[dataSize];
            Random.Shared.NextBytes(testData);

            // Write in chunks
            var writeTask = Task.Run(async () =>
            {
                const int chunkSize = 16 * 1024;
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
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectAsync_WithClientOptions_AppliesOptions()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            // Act
            await using var client = await WebSocketMultiplexer.ConnectAsync(
                $"ws://localhost:{port}/ws",
                options: null,
                clientOptions: opts =>
                {
                    opts.SetRequestHeader("X-Custom-Header", "test-value");
                });

            // Assert
            Assert.Equal(WebSocketState.Open, client.State);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    #region Extension Method Tests

    [Fact(Timeout = 120000)]
    public async Task AsMux_Extension_WrapsExistingWebSocket()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Connect client which triggers server to accept
            await using var clientConnection = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);

            // Act - Use extension method
            await using var serverConnection = serverWebSocket.AsMux();

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));

            var writeChannel = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
            var readChannel = await serverConnection.AcceptChannelAsync("test", cts.Token);

            var testData = "AsMux extension test"u8.ToArray();
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
            Assert.Equal(WebSocketState.Open, serverConnection.State);

            await cts.CancelAsync();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectMuxAsync_Extension_ConnectsAndCreatesMultiplexer()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Act - Use extension method
            var clientWebSocket = new ClientWebSocket();
            await using var clientConnection = await clientWebSocket.ConnectMuxAsync($"ws://localhost:{port}/ws");
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = serverWebSocket.AsMux();

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));

            var writeChannel = await clientConnection.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
            var readChannel = await serverConnection.AcceptChannelAsync("test", cts.Token);

            var testData = "ConnectMuxAsync extension test"u8.ToArray();
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
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ConnectMuxAsync_WithUri_Extension_ConnectsAndCreatesMultiplexer()
    {
        // Arrange
        var (host, port, wsChannel) = await CreateServerAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Act - Use extension method with Uri
            var clientWebSocket = new ClientWebSocket();
            await using var clientConnection = await clientWebSocket.ConnectMuxAsync(new Uri($"ws://localhost:{port}/ws"));
            var serverWebSocket = await wsChannel.Reader.ReadAsync(cts.Token);
            await using var serverConnection = serverWebSocket.AsMux();

            var startTasks = await Task.WhenAll(clientConnection.StartAsync(cts.Token), serverConnection.StartAsync(cts.Token));

            // Assert
            Assert.Equal(WebSocketState.Open, clientConnection.State);

            await cts.CancelAsync();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    #endregion
}
