using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using NetConduit;
using NetConduit.Tcp;

/// <summary>
/// Bottleneck analysis using high-resolution timing instrumentation.
/// Instruments the write path to measure exactly where time is spent.
/// 
/// Usage:
///   dotnet run -c Release -- bottleneck [scenario] [channels] [msgSize] [durationSec]
/// 
/// Examples:
///   dotnet run -c Release -- bottleneck game-tick 50 64 5
///   dotnet run -c Release -- bottleneck game-tick 1 256 5
/// </summary>
public static class BottleneckAnalysis
{
    // High-resolution timing buckets
    static long _totalWriteCalls;
    static long _totalWriteNs;
    
    // Read side
    static long _totalReadCalls;
    static long _totalReadNs;
    
    // Per-call histogram (in microseconds)
    static readonly ConcurrentBag<long> WriteLatenciesUs = [];
    static readonly ConcurrentBag<long> ReadLatenciesUs = [];

    public static async Task RunAsync(string[] args)
    {
        var scenario = args.Length > 1 ? args[1] : "game-tick";
        var channels = args.Length > 2 ? int.Parse(args[2]) : 50;
        var msgSize = args.Length > 3 ? int.Parse(args[3]) : 64;
        var durationSec = args.Length > 4 ? int.Parse(args[4]) : 5;

        Console.Error.WriteLine($"=== Bottleneck Analysis: {scenario} ch={channels} msg={msgSize}B duration={durationSec}s ===");
        Console.Error.WriteLine();

        await RunInstrumented(channels, msgSize, durationSec);
    }

    static async Task RunInstrumented(int channelCount, int msgSize, int durationSec)
    {
        var sendBuffer = new byte[msgSize];
        Random.Shared.NextBytes(sendBuffer);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec + 30));
        using var benchCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        var server = StreamMultiplexer.Create(serverOptions);
        _ = server.Start(cts.Token);

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        var client = StreamMultiplexer.Create(clientOptions);
        _ = client.Start(cts.Token);

        await Task.WhenAll(server.WaitForReadyAsync(cts.Token), client.WaitForReadyAsync(cts.Token));

        var readChannels = new ReadChannel[channelCount];
        var acceptTask = Task.Run(async () =>
        {
            for (int i = 0; i < channelCount; i++)
                readChannels[i] = await server.AcceptChannelAsync($"ch-{i}", cts.Token);
        }, cts.Token);

        var writeChannels = new WriteChannel[channelCount];
        for (int i = 0; i < channelCount; i++)
            writeChannels[i] = await client.OpenChannelAsync(new ChannelOptions { ChannelId = $"ch-{i}" }, cts.Token);
        await acceptTask;
        await Task.Delay(50, cts.Token);

        long totalMessages = 0;
        long totalReadMessages = 0;
        
        Console.Error.WriteLine("Running instrumented benchmark...");
        var sw = Stopwatch.StartNew();

        // Read tasks with timing
        var readTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = readChannels[i];
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[msgSize * 4];
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        var readSw = Stopwatch.StartNew();
                        var r = await ch.ReadAsync(buf, benchCts.Token);
                        readSw.Stop();
                        
                        if (r == 0) break;
                        
                        Interlocked.Add(ref _totalReadNs, readSw.Elapsed.Ticks * 100);
                        Interlocked.Increment(ref _totalReadCalls);
                        Interlocked.Increment(ref totalReadMessages);
                        
                        // Sample ~1% of calls for histogram
                        if (Volatile.Read(ref _totalReadCalls) % 100 == 0)
                            ReadLatenciesUs.Add(readSw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        // Write tasks with timing
        var writeTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = writeChannels[i];
            writeTasks[i] = Task.Run(async () =>
            {
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        var writeSw = Stopwatch.StartNew();
                        await ch.WriteAsync(sendBuffer, benchCts.Token);
                        writeSw.Stop();
                        
                        Interlocked.Increment(ref totalMessages);
                        Interlocked.Add(ref _totalWriteNs, writeSw.Elapsed.Ticks * 100);
                        Interlocked.Increment(ref _totalWriteCalls);
                        
                        // Sample ~1% of calls for histogram
                        if (Volatile.Read(ref _totalWriteCalls) % 100 == 0)
                            WriteLatenciesUs.Add(writeSw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        sw.Stop();

        foreach (var ch in writeChannels)
            try { await ch.CloseAsync(cts.Token); } catch { }
        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        var wallTime = sw.Elapsed;
        var mps = totalMessages / wallTime.TotalSeconds;

        Console.Error.WriteLine();
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine($"  BOTTLENECK ANALYSIS RESULTS");
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Scenario:     game-tick ch={channelCount} msg={msgSize}B");
        Console.Error.WriteLine($"  Wall time:    {wallTime.TotalSeconds:F2}s");
        Console.Error.WriteLine($"  Messages:     {totalMessages:N0} sent, {totalReadMessages:N0} received");
        Console.Error.WriteLine($"  Throughput:   {mps:F0} msg/s");
        Console.Error.WriteLine();
        
        // Write path breakdown
        Console.Error.WriteLine("--- WRITE PATH ---");
        var avgWriteUs = _totalWriteCalls > 0 ? _totalWriteNs / 1000.0 / _totalWriteCalls : 0;
        Console.Error.WriteLine($"  Total WriteAsync calls:   {_totalWriteCalls:N0}");
        Console.Error.WriteLine($"  Avg WriteAsync latency:   {avgWriteUs:F1} us");
        Console.Error.WriteLine($"  Theoretical max msg/s:    {(_totalWriteCalls > 0 ? channelCount * 1_000_000.0 / avgWriteUs : 0):F0} (if no other overhead)");
        Console.Error.WriteLine();

        // Read path breakdown
        Console.Error.WriteLine("--- READ PATH ---");
        var avgReadUs = _totalReadCalls > 0 ? _totalReadNs / 1000.0 / _totalReadCalls : 0;
        Console.Error.WriteLine($"  Total ReadAsync calls:    {_totalReadCalls:N0}");
        Console.Error.WriteLine($"  Avg ReadAsync latency:    {avgReadUs:F1} us");
        Console.Error.WriteLine();

        // Statistics from the multiplexer
        Console.Error.WriteLine("--- MULTIPLEXER STATS (CLIENT - WRITER SIDE) ---");
        var clientStats = client.Stats;
        Console.Error.WriteLine($"  Bytes sent:          {clientStats.BytesSent:N0}");
        Console.Error.WriteLine($"  Bytes received:      {clientStats.BytesReceived:N0}");
        Console.Error.WriteLine($"  Credit starvations:  {clientStats.TotalCreditStarvationEvents:N0}");
        Console.Error.WriteLine($"  Total credit wait:   {clientStats.TotalCreditWaitTime.TotalMilliseconds:F1} ms");
        Console.Error.WriteLine($"  Channels starving:   {clientStats.ChannelsCurrentlyWaitingForCredits}");
        Console.Error.WriteLine();

        Console.Error.WriteLine("--- MULTIPLEXER STATS (SERVER - READER SIDE) ---");
        var serverStats = server.Stats;
        Console.Error.WriteLine($"  Bytes sent:          {serverStats.BytesSent:N0}");
        Console.Error.WriteLine($"  Bytes received:      {serverStats.BytesReceived:N0}");
        Console.Error.WriteLine($"  Credit starvations:  {serverStats.TotalCreditStarvationEvents:N0}");
        Console.Error.WriteLine();

        // Latency histograms
        PrintHistogram("Write Latency Distribution", WriteLatenciesUs);
        PrintHistogram("Read Latency Distribution", ReadLatenciesUs);
        
        // Channel-level stats
        Console.Error.WriteLine("--- PER-CHANNEL WRITE STATS (sample) ---");
        for (int i = 0; i < Math.Min(5, channelCount); i++)
        {
            var chStats = writeChannels[i].Stats;
            Console.Error.WriteLine($"  ch-{i}: sent={chStats.BytesSent:N0}B frames={chStats.FramesSent:N0} credits_used={chStats.CreditsConsumed:N0} starvations={chStats.CreditStarvationCount}");
        }
        if (channelCount > 5)
            Console.Error.WriteLine($"  ... ({channelCount - 5} more channels)");
        Console.Error.WriteLine();
        
        // Key ratios
        Console.Error.WriteLine("--- KEY RATIOS ---");
        var writeWallRatio = _totalWriteNs / 1e9 / (wallTime.TotalSeconds * channelCount);
        Console.Error.WriteLine($"  Write busy ratio:    {writeWallRatio:P1} (time in WriteAsync / wall time per channel)");
        var readWallRatio = _totalReadNs / 1e9 / (wallTime.TotalSeconds * channelCount);
        Console.Error.WriteLine($"  Read busy ratio:     {readWallRatio:P1} (time in ReadAsync / wall time per channel)");
        Console.Error.WriteLine();
        
        // Theoretical analysis
        Console.Error.WriteLine("--- THEORETICAL ANALYSIS ---");
        var frameOverhead = 9; // 9-byte header
        var bytesPerMsg = msgSize + frameOverhead;
        var totalDataRate = mps * bytesPerMsg;
        Console.Error.WriteLine($"  Data rate:          {totalDataRate / 1_048_576.0:F1} MB/s ({totalDataRate / 1000.0:F0} KB/s)");
        Console.Error.WriteLine($"  Frame overhead:     {frameOverhead}B/msg ({(double)frameOverhead / (msgSize + frameOverhead):P1})");
        Console.Error.WriteLine($"  Effective payload:  {mps * msgSize / 1_048_576.0:F1} MB/s");
        Console.Error.WriteLine();

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }

    static void PrintHistogram(string title, ConcurrentBag<long> latencies)
    {
        if (latencies.IsEmpty)
        {
            Console.Error.WriteLine($"--- {title}: no samples ---");
            return;
        }

        var sorted = latencies.OrderBy(x => x).ToList();
        var p50 = sorted[(int)(sorted.Count * 0.50)];
        var p90 = sorted[(int)(sorted.Count * 0.90)];
        var p95 = sorted[(int)(sorted.Count * 0.95)];
        var p99 = sorted[(int)(sorted.Count * 0.99)];
        var max = sorted[^1];
        var min = sorted[0];
        var avg = sorted.Average();

        Console.Error.WriteLine($"--- {title} ({sorted.Count} samples, in microseconds) ---");
        Console.Error.WriteLine($"  Min:  {min:N0}   Avg:  {avg:N0}   P50:  {p50:N0}   P90:  {p90:N0}   P95:  {p95:N0}   P99:  {p99:N0}   Max:  {max:N0}");

        // Bucket distribution
        long[] buckets = [10, 50, 100, 500, 1000, 5000, 10000, 50000, 100000];
        Console.Error.Write("  Distribution: ");
        int prevCount = 0;
        foreach (var bucket in buckets)
        {
            var count = sorted.Count(x => x <= bucket);
            var delta = count - prevCount;
            if (delta > 0)
                Console.Error.Write($"<{bucket}us:{delta} ");
            prevCount = count;
        }
        var remaining = sorted.Count - prevCount;
        if (remaining > 0)
            Console.Error.Write($">100ms:{remaining}");
        Console.Error.WriteLine();
        Console.Error.WriteLine();
    }
}
