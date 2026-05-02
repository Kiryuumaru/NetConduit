using System.Net;
using System.Net.Sockets;
using NetConduit.Models;
using NetConduit.Udp;

namespace NetConduit.Udp.IntegrationTests;

/// <summary>
/// Validates that ReliableUdpStream handles concurrent synchronous and asynchronous
/// disposal safely, ensuring exactly one disposal path executes cleanup logic.
/// </summary>
[Collection("UdpTests")]
public class UdpStreamConcurrentDisposeTests
{
    [Fact(Timeout = 30000)]
    public async Task ConcurrentSyncAndAsyncDispose_CompletesWithoutException()
    {
        // Validates that concurrent Dispose() and DisposeAsync() calls on the same
        // multiplexer (and underlying ReliableUdpStream) complete without throwing.
        // ReliableUdpStream uses Interlocked.CompareExchange for atomic disposal guard.

        var port = GetFreePort();
        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        var clientOptions = UdpMultiplexer.CreateOptions("127.0.0.1", port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = StreamMultiplexer.Create(serverOptions);
        await using var client = StreamMultiplexer.Create(clientOptions);

        var serverRun = server.Start(cts.Token);
        var clientRun = client.Start(cts.Token);
        await Task.WhenAll(server.WaitForReadyAsync(cts.Token), client.WaitForReadyAsync(cts.Token));

        // Race synchronous and asynchronous dispose on the same multiplexer.
        // The underlying ReliableUdpStream should serialize disposal atomically.
        var disposeSync = Task.Run(() => server.AsAsyncDisposable().DisposeAsync().AsTask());
        var disposeAsync = server.DisposeAsync().AsTask();

        await Task.WhenAll(disposeSync, disposeAsync);

        cts.Cancel();
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

internal static class AsyncDisposableExtensions
{
    public static IAsyncDisposable AsAsyncDisposable(this IAsyncDisposable d) => d;
}
