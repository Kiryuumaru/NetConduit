using System.Net;
using System.Net.Sockets;
using NetConduit.Udp;

namespace NetConduit.Udp.IntegrationTests;

public class UdpMultiplexerTests
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact(Timeout = 30000)]
    public async Task SendsAndReceivesData()
    {
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

        var testData = "Hello, UDP Multiplexer!"u8.ToArray();
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
    }
}
