using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Tcp;

namespace NetConduit.Transport.Tcp.IntegrationTests;

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

    [Fact(Timeout = 30000)]
    public async Task ConnectAndAccept_EstablishesConnection()
    {
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        await using var server = StreamMultiplexer.Create(serverOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        listener.Stop();
    }

    [Fact(Timeout = 30000)]
    public async Task OpenChannel_SendsAndReceivesData()
    {
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        await using var server = StreamMultiplexer.Create(serverOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

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

        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, buffer);

        listener.Stop();
    }

    [Fact(Timeout = 30000)]
    public async Task MultipleChannels_TransferDataConcurrently()
    {
        int port = GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        await using var server = StreamMultiplexer.Create(serverOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        const int channelCount = 5;
        var tasks = new List<Task>();

        for (int i = 0; i < channelCount; i++)
        {
            var channelId = $"ch-{i}";
            var data = new byte[1024];
            Random.Shared.NextBytes(data);

            var writeChannel = client.OpenChannel(channelId);

            tasks.Add(Task.Run(async () =>
            {
                await writeChannel.WriteAsync(data, cts.Token);
                await writeChannel.CloseAsync(cts.Token);
            }));

            tasks.Add(Task.Run(async () =>
            {
                var readChannel = await server.AcceptChannelAsync(channelId, cts.Token);
                var received = new byte[data.Length];
                int totalRead = 0;
                while (totalRead < received.Length)
                {
                    int read = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }
                Assert.Equal(data.Length, totalRead);
                Assert.Equal(data, received);
            }));
        }

        await Task.WhenAll(tasks);
        listener.Stop();
    }
}
