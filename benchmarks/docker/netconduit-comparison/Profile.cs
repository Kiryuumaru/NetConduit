using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Tcp;

/// <summary>
/// Profiling harness for targeted scenario analysis.
/// Runs a single scenario for a configurable duration, suitable for dotnet-trace attachment.
/// 
/// Usage:
///   dotnet run -c Release -- profile [scenario] [channels] [msgSize] [durationSec]
/// 
/// Scenarios:
///   game-tick    Small message burst (default: 50ch, 64B, 10s)
///   throughput   Bulk data transfer (default: 10ch, 1MB, 10s)
/// 
/// Examples:
///   dotnet run -c Release -- profile game-tick 50 64 10
///   dotnet run -c Release -- profile game-tick 1 256 15
///   dotnet run -c Release -- profile throughput 10 1048576 10
/// </summary>
public static class Profile
{
    public static async Task RunAsync(string[] args)
    {
        var scenario = args.Length > 1 ? args[1] : "game-tick";
        var channels = args.Length > 2 ? int.Parse(args[2]) : 50;
        var msgSize = args.Length > 3 ? int.Parse(args[3]) : 64;
        var durationSec = args.Length > 4 ? int.Parse(args[4]) : 10;
        var noWait = args.Any(a => a == "--no-wait");

        Console.Error.WriteLine($"=== Profile: {scenario} ch={channels} msg={msgSize}B duration={durationSec}s ===");
        Console.Error.WriteLine($"PID: {Environment.ProcessId}");

        if (!noWait)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Attach profiler now, then press ENTER to start...");
            Console.Error.WriteLine($"  dotnet-trace collect -p {Environment.ProcessId} --providers Microsoft-DotNETCore-SampleProfiler --duration 00:00:{durationSec:D2} -o trace.nettrace");
            Console.Error.WriteLine();
            Console.ReadLine();
        }

        switch (scenario)
        {
            case "game-tick":
                await RunGameTickProfile(channels, msgSize, durationSec);
                break;
            case "throughput":
                await RunThroughputProfile(channels, msgSize, durationSec);
                break;
            default:
                Console.Error.WriteLine($"Unknown scenario: {scenario}");
                break;
        }
    }

    static async Task RunGameTickProfile(int channelCount, int msgSize, int durationSec)
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
        Console.Error.WriteLine("Running...");
        var sw = Stopwatch.StartNew();

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
                        var r = await ch.ReadAsync(buf, benchCts.Token);
                        if (r == 0) break;
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

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
                        await ch.WriteAsync(sendBuffer, benchCts.Token);
                        Interlocked.Increment(ref totalMessages);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        sw.Stop();

        var mps = totalMessages / sw.Elapsed.TotalSeconds;
        Console.Error.WriteLine($"Result: {mps:F0} msg/s ({totalMessages:N0} messages in {sw.Elapsed.TotalSeconds:F1}s)");

        foreach (var ch in writeChannels)
            try { await ch.CloseAsync(cts.Token); } catch { }
        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }

    static async Task RunThroughputProfile(int channelCount, int dataSize, int durationSec)
    {
        const int chunkSize = 64 * 1024;
        var sendBuffer = new byte[Math.Min(dataSize, chunkSize)];
        Random.Shared.NextBytes(sendBuffer);

        // For throughput, we loop for durationSec rather than sending fixed amount
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

        long totalBytes = 0;
        Console.Error.WriteLine("Running...");
        var sw = Stopwatch.StartNew();

        var readTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = readChannels[i];
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[chunkSize];
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        var r = await ch.ReadAsync(buf, benchCts.Token);
                        if (r == 0) break;
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

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
                        await ch.WriteAsync(sendBuffer.AsMemory(0, Math.Min(sendBuffer.Length, dataSize)), benchCts.Token);
                        Interlocked.Add(ref totalBytes, Math.Min(sendBuffer.Length, dataSize));
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        sw.Stop();

        var mbps = totalBytes / sw.Elapsed.TotalSeconds / 1_048_576.0;
        Console.Error.WriteLine($"Result: {mbps:F1} MB/s ({totalBytes / 1_048_576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s)");

        foreach (var ch in writeChannels)
            try { await ch.CloseAsync(cts.Token); } catch { }
        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }
}
