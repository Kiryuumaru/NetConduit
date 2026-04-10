using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetConduit;
using NetConduit.Tcp;

if (args.Length > 0 && args[0] == "diag")
{
    await Diagnostic.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "profile")
{
    await Profile.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "bottleneck")
{
    await BottleneckAnalysis.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "deep-profile")
{
    await DeepProfile.RunAsync(args);
    return;
}

const int Runs = 5;
var results = new List<object>();
var filter = args.Length > 0 ? args[0] : "";

if (filter is "" or "throughput")
{
// Throughput scenarios — same matrix as Go
int[] channelCounts = [1, 10, 100];
int[] dataSizes = [1024, 102_400, 1_048_576];

foreach (var channels in channelCounts)
{
    foreach (var dataSize in dataSizes)
    {
        // Raw TCP baseline
        var rawMbps = await RunThroughputRawTcpAsync(channels, dataSize, Runs);
        results.Add(MakeThroughputResult("Raw TCP (.NET)", channels, dataSize, rawMbps));
        Console.Error.WriteLine($"  throughput Raw TCP (.NET)       ch={channels,4} data={dataSize,8}  {rawMbps,8:F1} MB/s");

        // NetConduit Mux
        var muxMbps = await RunThroughputMuxAsync(channels, dataSize, Runs);
        results.Add(MakeThroughputResult("NetConduit Mux TCP", channels, dataSize, muxMbps));
        Console.Error.WriteLine($"  throughput NetConduit Mux TCP   ch={channels,4} data={dataSize,8}  {muxMbps,8:F1} MB/s");
    }
}
}

if (filter is "" or "game-tick")
{
// Game-tick scenarios — same matrix as Go
int[] gtChannels = [1, 10, 50, 1000];
int[] msgSizes = [64, 256];
const int GameTickDurationSec = 2;

foreach (var channels in gtChannels)
{
    foreach (var msgSize in msgSizes)
    {
        // Raw TCP baseline
        try
        {
            var rawMps = await RunGameTickRawTcpAsync(channels, msgSize, GameTickDurationSec, Runs);
            results.Add(MakeGameTickResult("Raw TCP (.NET)", channels, msgSize, rawMps));
            Console.Error.WriteLine($"  game-tick  Raw TCP (.NET)       ch={channels,4} msg={msgSize,4}  {rawMps,8:F0} msg/s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  game-tick  Raw TCP (.NET)       ch={channels,4} msg={msgSize,4}  FAILED: {ex.GetType().Name}");
        }

        // NetConduit Mux
        try
        {
            var muxMps = await RunGameTickMuxAsync(channels, msgSize, GameTickDurationSec, Runs);
            results.Add(MakeGameTickResult("NetConduit Mux TCP", channels, msgSize, muxMps));
            Console.Error.WriteLine($"  game-tick  NetConduit Mux TCP   ch={channels,4} msg={msgSize,4}  {muxMps,8:F0} msg/s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  game-tick  NetConduit Mux TCP   ch={channels,4} msg={msgSize,4}  FAILED: {ex.GetType().Name}");
        }
    }
}
}

Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
return;

// ---- Result helpers ----

static object MakeThroughputResult(string impl, int channels, int dataSize, double mbps)
{
    var totalBytes = (long)channels * dataSize;
    return new
    {
        implementation = impl,
        scenario = "throughput",
        channels,
        dataSizeBytes = dataSize,
        totalBytes,
        durationMs = totalBytes / mbps / 1_048_576.0 * 1000.0,
        throughputMBps = mbps,
        messagesPerSec = 0.0,
        allocMB = 0.0
    };
}

static object MakeGameTickResult(string impl, int channels, int msgSize, double mps)
{
    return new
    {
        implementation = impl,
        scenario = "game-tick",
        channels,
        dataSizeBytes = msgSize,
        totalBytes = 0L,
        durationMs = 0.0,
        throughputMBps = 0.0,
        messagesPerSec = mps,
        allocMB = 0.0
    };
}

// ==================================================================
// Throughput: Raw TCP (N separate connections)
// ==================================================================
static async Task<double> RunThroughputRawTcpAsync(int channelCount, int dataSize, int runs)
{
    const int chunkSize = 64 * 1024;
    var sendBuffer = new byte[Math.Min(dataSize, chunkSize)];
    Random.Shared.NextBytes(sendBuffer);
    var measurements = new List<double>();

    for (int run = 0; run < runs; run++)
    {
        var listeners = new TcpListener[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            listeners[i] = new TcpListener(IPAddress.Loopback, 0);
            listeners[i].Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listeners[i].Start();
        }

        var sw = Stopwatch.StartNew();

        var readTasks = new Task[channelCount];
        var writeTasks = new Task[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            var li = listeners[i];
            readTasks[i] = Task.Run(async () =>
            {
                using var client = await li.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buf = new byte[chunkSize];
                int total = 0;
                while (total < dataSize)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n == 0) break;
                    total += n;
                }
            });

            var port = ((IPEndPoint)li.LocalEndpoint).Port;
            writeTasks[i] = Task.Run(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await using var stream = client.GetStream();
                int total = 0;
                while (total < dataSize)
                {
                    var toSend = Math.Min(sendBuffer.Length, dataSize - total);
                    await stream.WriteAsync(sendBuffer.AsMemory(0, toSend));
                    total += toSend;
                }
            });
        }

        await Task.WhenAll(writeTasks);
        await Task.WhenAll(readTasks);
        sw.Stop();

        var totalBytes = (long)channelCount * dataSize;
        measurements.Add(totalBytes / sw.Elapsed.TotalSeconds / 1_048_576.0);

        foreach (var li in listeners) li.Stop();
    }

    measurements.Sort();
    return measurements[measurements.Count / 2];
}

// ==================================================================
// Throughput: NetConduit Mux TCP
// ==================================================================
static async Task<double> RunThroughputMuxAsync(int channelCount, int dataSize, int runs)
{
    const int chunkSize = 64 * 1024;
    var sendBuffer = new byte[Math.Min(dataSize, chunkSize)];
    Random.Shared.NextBytes(sendBuffer);
    var measurements = new List<double>();

    for (int run = 0; run < runs; run++)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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
        await Task.Delay(20, cts.Token);

        var sw = Stopwatch.StartNew();

        var readTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = readChannels[i];
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[chunkSize];
                long total = 0;
                while (total < dataSize)
                {
                    var r = await ch.ReadAsync(buf, cts.Token);
                    if (r == 0) break;
                    total += r;
                }
            }, cts.Token);
        }

        var writeTasks = new Task[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var ch = writeChannels[i];
            writeTasks[i] = Task.Run(async () =>
            {
                long total = 0;
                while (total < dataSize)
                {
                    var toSend = (int)Math.Min(sendBuffer.Length, dataSize - total);
                    await ch.WriteAsync(sendBuffer.AsMemory(0, toSend), cts.Token);
                    total += toSend;
                }
                await ch.CloseAsync(cts.Token);
            }, cts.Token);
        }

        await Task.WhenAll(writeTasks);
        await Task.WhenAll(readTasks);
        sw.Stop();

        var totalBytes = (long)channelCount * dataSize;
        measurements.Add(totalBytes / sw.Elapsed.TotalSeconds / 1_048_576.0);

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }

    measurements.Sort();
    return measurements[measurements.Count / 2];
}

// ==================================================================
// Game-Tick: Raw TCP
// ==================================================================
static async Task<double> RunGameTickRawTcpAsync(int channelCount, int msgSize, int durationSec, int runs)
{
    var sendBuffer = new byte[msgSize];
    Random.Shared.NextBytes(sendBuffer);
    var measurements = new List<double>();

    for (int run = 0; run < runs; run++)
    {
        var listeners = new TcpListener[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            listeners[i] = new TcpListener(IPAddress.Loopback, 0);
            listeners[i].Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listeners[i].Start();
        }

        using var benchCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));
        long totalMessages = 0;

        var readTasks = new Task[channelCount];
        var writeTasks = new Task[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            var li = listeners[i];
            readTasks[i] = Task.Run(async () =>
            {
                using var client = await li.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buf = new byte[msgSize * 4];
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        var n = await stream.ReadAsync(buf, benchCts.Token);
                        if (n == 0) break;
                    }
                }
                catch (OperationCanceledException) { }
            });

            var port = ((IPEndPoint)li.LocalEndpoint).Port;
            writeTasks[i] = Task.Run(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await using var stream = client.GetStream();
                try
                {
                    while (!benchCts.Token.IsCancellationRequested)
                    {
                        await stream.WriteAsync(sendBuffer, benchCts.Token);
                        Interlocked.Increment(ref totalMessages);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(writeTasks);
        sw.Stop();

        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        measurements.Add(totalMessages / sw.Elapsed.TotalSeconds);
        foreach (var li in listeners) li.Stop();
    }

    measurements.Sort();
    return measurements[measurements.Count / 2];
}

// ==================================================================
// Game-Tick: NetConduit Mux TCP
// ==================================================================
static async Task<double> RunGameTickMuxAsync(int channelCount, int msgSize, int durationSec, int runs)
{
    var sendBuffer = new byte[msgSize];
    Random.Shared.NextBytes(sendBuffer);
    var measurements = new List<double>();

    for (int run = 0; run < runs; run++)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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
        await Task.Delay(20, cts.Token);

        // Start benchmark timer AFTER setup completes
        using var benchCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        long totalMessages = 0;
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

        foreach (var ch in writeChannels)
            try { await ch.CloseAsync(cts.Token); } catch { }
        try { await Task.WhenAny(Task.WhenAll(readTasks), Task.Delay(2000)); } catch { }

        measurements.Add(totalMessages / sw.Elapsed.TotalSeconds);

        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }

    measurements.Sort();
    return measurements[measurements.Count / 2];
}
