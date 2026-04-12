using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Internal;
using NetConduit.Tcp;

/// <summary>
/// Deep profiling using instrumented hot paths.
/// Enables HotPathProfiler to measure actual costs of each operation
/// on both read and write paths.
///
/// Usage:
///   dotnet run -c Release -- deep-profile [channels] [msgSize] [durationSec]
///
/// Examples:
///   dotnet run -c Release -- deep-profile 1 64 5
///   dotnet run -c Release -- deep-profile 50 64 5
///   dotnet run -c Release -- deep-profile 1000 256 5
/// </summary>
public static class DeepProfile
{
    public static async Task RunAsync(string[] args)
    {
        var channels = args.Length > 1 ? int.Parse(args[1]) : 50;
        var msgSize = args.Length > 2 ? int.Parse(args[2]) : 64;
        var durationSec = args.Length > 3 ? int.Parse(args[3]) : 5;

        Console.Error.WriteLine($"=== Deep Profile: ch={channels} msg={msgSize}B duration={durationSec}s ===");
        Console.Error.WriteLine($"PID: {Environment.ProcessId}");
        Console.Error.WriteLine();

        // Collect GC baseline
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memBefore = GC.GetTotalMemory(false);

        // Enable profiling
        HotPathProfiler.Reset();
        HotPathProfiler.Enable();

        var sendBuffer = new byte[msgSize];
        Random.Shared.NextBytes(sendBuffer);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec + 30));

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        var server = StreamMultiplexer.Create(serverOptions);
        _ = server.Start(cts.Token);

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        var client = StreamMultiplexer.Create(clientOptions);
        _ = client.Start(cts.Token);

        await Task.WhenAll(server.WaitForReadyAsync(cts.Token), client.WaitForReadyAsync(cts.Token));

        var readChannels = new ReadChannel[channels];
        var acceptTask = Task.Run(async () =>
        {
            for (int i = 0; i < channels; i++)
                readChannels[i] = await server.AcceptChannelAsync($"ch-{i}", cts.Token);
        }, cts.Token);

        var writeChannels = new WriteChannel[channels];
        for (int i = 0; i < channels; i++)
            writeChannels[i] = await client.OpenChannelAsync(new ChannelOptions { ChannelId = $"ch-{i}" }, cts.Token);
        await acceptTask;
        await Task.Delay(50, cts.Token);

        // Start benchmark timer AFTER setup completes
        using var benchCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        long totalSent = 0;
        long totalRead = 0;

        Console.Error.WriteLine("Running instrumented benchmark...");
        var sw = Stopwatch.StartNew();

        // Read tasks
        var readTasks = new Task[channels];
        for (int i = 0; i < channels; i++)
        {
            var ch = readChannels[i];
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[msgSize * 4];
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        var r = await ch.ReadAsync(buf, benchCts.Token);
                        if (r == 0) break;
                        Interlocked.Increment(ref totalRead);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        // Write tasks
        var writeTasks = new Task[channels];
        for (int i = 0; i < channels; i++)
        {
            var ch = writeChannels[i];
            writeTasks[i] = Task.Run(async () =>
            {
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        await ch.WriteAsync(sendBuffer, benchCts.Token);
                        Interlocked.Increment(ref totalSent);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        sw.Stop();

        HotPathProfiler.Disable();

        foreach (var ch in writeChannels)
            try { await ch.CloseAsync(cts.Token); } catch { }
        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        // GC stats
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var memAfter = GC.GetTotalMemory(false);

        var wallTime = sw.Elapsed.TotalSeconds;
        var mps = totalSent / wallTime;

        Console.Error.WriteLine();
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine("  DEEP PROFILE SUMMARY");
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine($"  Channels:       {channels}");
        Console.Error.WriteLine($"  Message size:   {msgSize}B");
        Console.Error.WriteLine($"  Wall time:      {wallTime:F2}s");
        Console.Error.WriteLine($"  Sent:           {totalSent:N0} messages ({mps:F0} msg/s)");
        Console.Error.WriteLine($"  Read:           {totalRead:N0} messages");
        Console.Error.WriteLine();
        Console.Error.WriteLine("--- GC PRESSURE ---");
        Console.Error.WriteLine($"  Gen0 collections: {gen0After - gen0Before}");
        Console.Error.WriteLine($"  Gen1 collections: {gen1After - gen1Before}");
        Console.Error.WriteLine($"  Gen2 collections: {gen2After - gen2Before}");
        Console.Error.WriteLine($"  Memory delta:     {(memAfter - memBefore) / 1024.0:F0} KB");
        Console.Error.WriteLine($"  Gen0/sec:         {(gen0After - gen0Before) / wallTime:F1}");
        Console.Error.WriteLine();

        // Print hot path profiler results
        HotPathProfiler.PrintReport(wallTime);

        // Multiplexer stats
        Console.Error.WriteLine("--- MULTIPLEXER STATS ---");
        Console.Error.WriteLine($"  Client bytes sent:     {client.Stats.BytesSent:N0}");
        Console.Error.WriteLine($"  Server bytes received: {server.Stats.BytesReceived:N0}");
        Console.Error.WriteLine($"  Credit starvations:    {client.Stats.TotalCreditStarvationEvents:N0}");
        Console.Error.WriteLine();

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }
}
