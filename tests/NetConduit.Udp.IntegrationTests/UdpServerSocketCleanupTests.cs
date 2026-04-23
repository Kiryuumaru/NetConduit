using System.Net;
using System.Net.Sockets;
using NetConduit.Udp;

namespace NetConduit.Udp.IntegrationTests;

[Collection("UdpTests")]
public class UdpServerSocketCleanupTests
{
    [Fact(Timeout = 30000)]
    public async Task ServerStreamFactory_ReleasesSocketOnCancellation()
    {
        // When the server StreamFactory is cancelled during ReceiveAsync,
        // the bound UdpClient should be disposed so the port is released.
        var port = GetFreePort();
        var options = UdpMultiplexer.CreateServerOptions(port);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => options.StreamFactory(cts.Token));

        // If the socket was properly released, we can bind to the same port again.
        using var probe = new UdpClient(AddressFamily.InterNetworkV6);
        probe.Client.DualMode = true;
        probe.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
    }

    [Fact(Timeout = 30000)]
    public async Task ClientStreamFactory_ReleasesSocketOnCancellation()
    {
        // When the client StreamFactory is cancelled during ConnectAsync,
        // the allocated UdpClient should be disposed immediately (not left for GC finalizer).
        // Run multiple iterations WITHOUT triggering GC to accumulate leaked sockets.
        var fdCountBefore = CountOpenFileDescriptors();
        const int iterations = 20;

        for (int i = 0; i < iterations; i++)
        {
            var options = UdpMultiplexer.CreateOptions("127.0.0.1", GetFreePort());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await options.StreamFactory(cts.Token);
            }
            catch (OperationCanceledException) { }
        }

        // Do NOT call GC.Collect — we are verifying deterministic disposal, not finalizer cleanup.
        var fdCountAfter = CountOpenFileDescriptors();
        Assert.True(fdCountAfter <= fdCountBefore + 2,
            $"Socket leak detected over {iterations} iterations: fd count went from {fdCountBefore} to {fdCountAfter}");
    }

    private static int CountOpenFileDescriptors()
    {
        var path = $"/proc/{Environment.ProcessId}/fd";
        if (Directory.Exists(path))
            return Directory.GetFiles(path).Length;
        return -1;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
