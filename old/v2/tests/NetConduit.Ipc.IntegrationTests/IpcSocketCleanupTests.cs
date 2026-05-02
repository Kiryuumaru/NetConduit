using System.Net.Sockets;
using NetConduit.Ipc;

namespace NetConduit.Ipc.IntegrationTests;

/// <summary>
/// Tests that IPC Unix socket resources are properly cleaned up on cancellation and disposal.
/// </summary>
public class IpcSocketCleanupTests
{
    [Fact(Timeout = 30000)]
    public async Task ServerAcceptCancelled_SocketFileCleanedUp()
    {
        if (OperatingSystem.IsWindows()) return;

        var endpoint = Path.Combine(Path.GetTempPath(), $"netconduit-cleanup-test-{Guid.NewGuid():N}.sock");

        try
        {
            using var cts = new CancellationTokenSource();

            var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);

            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            try
            {
                await serverOptions.StreamFactory!(cts.Token);
                Assert.Fail("Should have been cancelled");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.False(File.Exists(endpoint),
                $"Unix socket file should be cleaned up after cancellation but still exists at: {endpoint}");
        }
        finally
        {
            if (File.Exists(endpoint))
                File.Delete(endpoint);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ServerAcceptCancelled_CanRebindSameEndpoint()
    {
        if (OperatingSystem.IsWindows()) return;

        var endpoint = Path.Combine(Path.GetTempPath(), $"netconduit-rebind-test-{Guid.NewGuid():N}.sock");

        try
        {
            using var cts = new CancellationTokenSource();

            var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);

            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            try
            {
                await serverOptions.StreamFactory!(cts.Token);
                Assert.Fail("Should have been cancelled");
            }
            catch (OperationCanceledException)
            {
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                var endPoint = new UnixDomainSocketEndPoint(endpoint);
                socket.Bind(endPoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Assert.Fail("Socket file was not cleaned up after cancellation — 'Address already in use'");
            }
            finally
            {
                socket.Dispose();
            }
        }
        finally
        {
            if (File.Exists(endpoint))
                File.Delete(endpoint);
        }
    }
}
