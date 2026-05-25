using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Tcp;

namespace NetConduit.Transport.Tcp.IntegrationTests;

public class TcpMultiplexerFactoryLeakTests
{
    /// <summary>
    /// Regression for: TcpMultiplexer.CreateOptions(host, port) StreamFactory must dispose
    /// the constructed TcpClient when ConnectAsync throws. Pre-fix, each failed connect leaked
    /// the underlying Socket until GC finalization, causing handle/FD growth under retry loops.
    /// Uses cancellation-during-connect as the failure trigger to keep each iteration fast and
    /// deterministic across platforms.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateOptions_FactoryCancelledDuringConnect_DoesNotLeakHandles()
    {
        // Use a non-routable RFC 5737 TEST-NET-1 address so ConnectAsync blocks until cancelled.
        var opts = TcpMultiplexer.CreateOptions("192.0.2.1", 1);

        await RunLeakProbe(opts);
    }

    /// <summary>
    /// Same regression coverage for the IPEndPoint overload.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateOptions_EndpointOverload_FactoryCancelledDuringConnect_DoesNotLeakHandles()
    {
        var opts = TcpMultiplexer.CreateOptions(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 1));

        await RunLeakProbe(opts);
    }

    private static async Task RunLeakProbe(NetConduit.Models.MultiplexerOptions opts)
    {
        // Warm up: drain JIT and one-time allocations.
        for (int i = 0; i < 3; i++)
        {
            using var warmupCts = new CancellationTokenSource(50);
            try { _ = await opts.StreamFactory(warmupCts.Token); }
            catch { }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long handlesBefore = proc.HandleCount;

        const int iterations = 50;
        for (int i = 0; i < iterations; i++)
        {
            using var cts = new CancellationTokenSource(50);
            try { _ = await opts.StreamFactory(cts.Token); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        // Do NOT GC. The point is that handles must be released eagerly by Dispose,
        // not lazily by finalization. Pre-fix the leaked socket handles stay live;
        // post-fix they are released when the catch in the factory calls client.Dispose().
        proc.Refresh();
        long handlesAfter = proc.HandleCount;
        long growth = handlesAfter - handlesBefore;

        // Each leaked TcpClient retains ~1 OS handle. 50 cancelled connects with no Dispose
        // would grow handles by ~50. Allow generous slack for unrelated process noise.
        Assert.True(growth < iterations / 2,
            $"Handle count grew by {growth} over {iterations} failed connects — suggests TcpClient leak. " +
            $"Before={handlesBefore}, After={handlesAfter}.");
    }
}

