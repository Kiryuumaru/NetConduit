using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Diagnostic test that isolates each operation type to identify
/// which specific operations cause memory growth.
/// Outputs per-operation memory allocation data.
/// </summary>
[Collection("Sequential")]
public sealed class MemoryLeakDiagnosticTests
{
    private const int IterationsPerOp = 500;

    private static long _channelCounter;

    private static string NextId(string prefix)
    {
        long id = Interlocked.Increment(ref _channelCounter);
        return $"{prefix}-{id}";
    }

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        return (client, server);
    }

    [Fact]
    public async Task Diagnostic_RapidOpenClose_MemoryProfile()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Server: accept and drain
        var serverTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[1024];
                        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                    }
                    catch { }
                    finally
                    {
                        await ch.DisposeAsync();
                    }
                }, CancellationToken.None);
            }
        }, CancellationToken.None);

        // Warm up
        for (int i = 0; i < 50; i++)
        {
            var w = client.OpenChannel(NextId("warmup"));
            await w.DisposeAsync();
        }
        await Task.Delay(1000, cts.Token);

        // Baseline measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long baselineBytes = GC.GetTotalMemory(true);
        long baselineAllocated = GC.GetTotalAllocatedBytes(precise: true);
        int baselineOpenChannels = client.Stats.OpenChannels;

        // Run iterations
        for (int i = 0; i < IterationsPerOp; i++)
        {
            var writer = client.OpenChannel(NextId("diag-rapid"));
            await writer.DisposeAsync();
        }

        await Task.Delay(2000, cts.Token);

        // Post measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long afterBytes = GC.GetTotalMemory(true);
        long afterAllocated = GC.GetTotalAllocatedBytes(precise: true);
        int afterOpenChannels = client.Stats.OpenChannels;
        int totalOpened = client.Stats.TotalChannelsOpened;
        int totalClosed = client.Stats.TotalChannelsClosed;

        await cts.CancelAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
        try { await serverTask; } catch { }

        long retainedGrowth = afterBytes - baselineBytes;
        long totalAllocated = afterAllocated - baselineAllocated;

        // Report as assertion message
        Assert.Fail(
            $"[DIAGNOSTIC] RapidOpenClose ({IterationsPerOp} iterations)\n" +
            $"  Baseline retained: {baselineBytes / 1024}KB\n" +
            $"  After retained:    {afterBytes / 1024}KB\n" +
            $"  Retained growth:   {retainedGrowth / 1024}KB ({(double)retainedGrowth / IterationsPerOp:F0} bytes/channel)\n" +
            $"  Total allocated:   {totalAllocated / 1024}KB ({(double)totalAllocated / IterationsPerOp:F0} bytes/channel)\n" +
            $"  Open channels (before): {baselineOpenChannels}\n" +
            $"  Open channels (after):  {afterOpenChannels}\n" +
            $"  Total opened: {totalOpened}\n" +
            $"  Total closed: {totalClosed}\n" +
            $"  LEAK INDICATOR: channels not closed = {totalOpened - totalClosed}");
    }

    [Fact]
    public async Task Diagnostic_HighVolume_5000Channels_MemoryProfile()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        const int channelCount = 5000;

        // Server: accept, drain, dispose
        var handlerTasks = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var serverTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var t = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[1024];
                        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                    }
                    catch { }
                    finally
                    {
                        await ch.DisposeAsync();
                    }
                }, CancellationToken.None);
                handlerTasks.Add(t);
            }
        }, CancellationToken.None);

        // Warm up
        for (int i = 0; i < 100; i++)
        {
            var w = client.OpenChannel(NextId("warmup-hv"));
            await w.DisposeAsync();
        }
        await Task.Delay(2000, cts.Token);
        try { await Task.WhenAll(handlerTasks); } catch { }
        handlerTasks.Clear();

        // Baseline measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long baselineBytes = GC.GetTotalMemory(true);

        // Run 5000 channels sequentially
        for (int i = 0; i < channelCount; i++)
        {
            var writer = client.OpenChannel(NextId("hv"));
            await writer.WriteAsync(new byte[256], cts.Token);
            await writer.DisposeAsync();
        }

        // Wait for server to process all
        await Task.Delay(5000, cts.Token);
        await cts.CancelAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
        try { await Task.WhenAll(handlerTasks); } catch { }

        // Final measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long afterBytes = GC.GetTotalMemory(true);

        long retainedGrowth = afterBytes - baselineBytes;
        double growthFactor = (double)afterBytes / baselineBytes;

        Assert.Fail(
            $"[DIAGNOSTIC] HighVolume {channelCount} channels (sequential open+write+close)\n" +
            $"  Baseline retained: {baselineBytes / 1024}KB\n" +
            $"  After retained:    {afterBytes / 1024}KB\n" +
            $"  Retained growth:   {retainedGrowth / 1024}KB ({(double)retainedGrowth / channelCount:F0} bytes/channel)\n" +
            $"  Growth factor:     {growthFactor:F2}x");
    }

    [Fact]
    public async Task Diagnostic_DataFlood_MemoryProfile()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Server: accept and drain
        var serverTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[8192];
                        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                    }
                    catch { }
                    finally
                    {
                        await ch.DisposeAsync();
                    }
                }, CancellationToken.None);
            }
        }, CancellationToken.None);

        // Warm up
        for (int i = 0; i < 20; i++)
        {
            var w = client.OpenChannel(NextId("warmup-flood"));
            await w.WriteAsync(new byte[4096], cts.Token);
            await w.DisposeAsync();
        }
        await Task.Delay(1000, cts.Token);

        // Baseline
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long baselineBytes = GC.GetTotalMemory(true);
        long baselineAllocated = GC.GetTotalAllocatedBytes(precise: true);
        int baselineOpenChannels = client.Stats.OpenChannels;

        // Run iterations: write 16KB per channel
        for (int i = 0; i < IterationsPerOp; i++)
        {
            var writer = client.OpenChannel(NextId("diag-flood"));
            var data = new byte[16384];
            await writer.WriteAsync(data, cts.Token);
            await writer.DisposeAsync();
        }

        await Task.Delay(2000, cts.Token);

        // Post measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long afterBytes = GC.GetTotalMemory(true);
        long afterAllocated = GC.GetTotalAllocatedBytes(precise: true);
        int afterOpenChannels = client.Stats.OpenChannels;
        int totalOpened = client.Stats.TotalChannelsOpened;
        int totalClosed = client.Stats.TotalChannelsClosed;

        await cts.CancelAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
        try { await serverTask; } catch { }

        long retainedGrowth = afterBytes - baselineBytes;
        long totalAllocated = afterAllocated - baselineAllocated;

        Assert.Fail(
            $"[DIAGNOSTIC] DataFlood 16KB ({IterationsPerOp} iterations)\n" +
            $"  Baseline retained: {baselineBytes / 1024}KB\n" +
            $"  After retained:    {afterBytes / 1024}KB\n" +
            $"  Retained growth:   {retainedGrowth / 1024}KB ({(double)retainedGrowth / IterationsPerOp:F0} bytes/channel)\n" +
            $"  Total allocated:   {totalAllocated / 1024}KB ({(double)totalAllocated / IterationsPerOp:F0} bytes/channel)\n" +
            $"  Open channels (before): {baselineOpenChannels}\n" +
            $"  Open channels (after):  {afterOpenChannels}\n" +
            $"  Total opened: {totalOpened}\n" +
            $"  Total closed: {totalClosed}\n" +
            $"  LEAK INDICATOR: channels not closed = {totalOpened - totalClosed}");
    }

    [Fact]
    public async Task Diagnostic_ChannelRegistryGrowth_Evidence()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Server: accept, read to EOF, then check if channels are actually freed
        var serverChannelCount = 0;
        var serverTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                Interlocked.Increment(ref serverChannelCount);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[1024];
                        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                    }
                    catch { }
                    finally
                    {
                        await ch.DisposeAsync();
                    }
                }, CancellationToken.None);
            }
        }, CancellationToken.None);

        // Open and close 200 channels, ensure they're drained
        for (int i = 0; i < 200; i++)
        {
            var writer = client.OpenChannel(NextId("reg-test"));
            await writer.WriteAsync(new byte[64], cts.Token);
            await writer.DisposeAsync();
        }

        // Wait for server to process
        await Task.Delay(3000, cts.Token);

        // Capture channel state
        int clientOpenChannels = client.Stats.OpenChannels;
        int serverOpenChannels = server.Stats.OpenChannels;
        int clientTotalOpened = client.Stats.TotalChannelsOpened;
        int clientTotalClosed = client.Stats.TotalChannelsClosed;
        int serverTotalOpened = server.Stats.TotalChannelsOpened;
        int serverTotalClosed = server.Stats.TotalChannelsClosed;
        var clientActiveIds = client.ActiveChannelIds.Count;
        var serverActiveIds = server.ActiveChannelIds.Count;

        await cts.CancelAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
        try { await serverTask; } catch { }

        Assert.Fail(
            $"[DIAGNOSTIC] Channel Registry State after 200 open+close+drain cycles:\n" +
            $"  CLIENT:\n" +
            $"    Open channels (Stats):    {clientOpenChannels}\n" +
            $"    Active channel IDs count: {clientActiveIds}\n" +
            $"    Total opened:             {clientTotalOpened}\n" +
            $"    Total closed:             {clientTotalClosed}\n" +
            $"    Not closed:               {clientTotalOpened - clientTotalClosed}\n" +
            $"  SERVER:\n" +
            $"    Open channels (Stats):    {serverOpenChannels}\n" +
            $"    Active channel IDs count: {serverActiveIds}\n" +
            $"    Total opened:             {serverTotalOpened}\n" +
            $"    Total closed:             {serverTotalClosed}\n" +
            $"    Not closed:               {serverTotalOpened - serverTotalClosed}\n" +
            $"    Server channels accepted: {serverChannelCount}");
    }
}
