using System.Diagnostics;
using NetConduit.Transport.Ipc;

namespace NetConduit.Transport.Ipc.IntegrationTests;

public class IpcMultiplexerFactoryLeakTests
{
    /// <summary>
    /// Regression for: IpcMultiplexer.CreateOptions StreamFactory must dispose the
    /// constructed TcpClient (Windows) / Socket (Unix) when ConnectAsync fails or is cancelled.
    /// Pre-fix, each failed connect leaked the underlying handle until GC finalization, causing
    /// FD growth under reconnect-storm conditions.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateOptions_FactoryCancelledDuringConnect_DoesNotLeakHandles()
    {
        // No server is listening at this endpoint; ConnectAsync will either fail with
        // ConnectionRefused (Windows: bound port empty) or block (Unix: missing socket file)
        // until cancellation. Both paths must release the handle.
        var endpoint = OperatingSystem.IsWindows()
            ? $"netconduit-leak-probe-{Guid.NewGuid():N}"
            : Path.Combine(Path.GetTempPath(), $"nc-leak-probe-{Guid.NewGuid():N}.sock");

        var opts = IpcMultiplexer.CreateOptions(endpoint);

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
            catch { }
        }

        // Do NOT GC. Handles must be released eagerly by Dispose, not lazily by finalization.
        proc.Refresh();
        long handlesAfter = proc.HandleCount;
        long growth = handlesAfter - handlesBefore;

        Assert.True(growth < iterations / 2,
            $"Handle count grew by {growth} over {iterations} failed connects — suggests TcpClient/Socket leak. " +
            $"Before={handlesBefore}, After={handlesAfter}.");
    }
}
