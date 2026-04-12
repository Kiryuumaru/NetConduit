using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Tcp;

/// <summary>
/// Quick diagnostic to measure where time is spent in ch=1 100KB throughput scenario.
/// Run with: dotnet run --project benchmarks/docker/netconduit-comparison -- diag
/// </summary>
public static class Diagnostic
{
    public static async Task RunAsync()
    {
        Console.Error.WriteLine("=== ch=1, 100KB diagnostic ===");

        var sendBuffer = new byte[1_048_576];
        Random.Shared.NextBytes(sendBuffer);

        for (int run = 0; run < 5; run++)
        {
            Console.Error.WriteLine($"\n--- Run {run + 1} ---");
            await RunOnce(sendBuffer);
        }
    }

    static async Task RunOnce(byte[] sendBuffer)
    {
        const int dataSize = 102_400;
        const int chunkSize = 64 * 1024;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
        var server = StreamMultiplexer.Create(serverOptions);
        _ = server.Start(cts.Token);

        var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port);
        var client = StreamMultiplexer.Create(clientOptions);
        _ = client.Start(cts.Token);

        await Task.WhenAll(server.WaitForReadyAsync(cts.Token), client.WaitForReadyAsync(cts.Token));

        var readChannel = default(ReadChannel);
        var acceptTask = Task.Run(async () =>
        {
            readChannel = await server.AcceptChannelAsync("ch-0", cts.Token);
        }, cts.Token);

        var writeChannel = await client.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch-0" }, cts.Token);
        await acceptTask;

        await Task.Delay(50, cts.Token);

        var overallSw = Stopwatch.StartNew();

        // PARALLEL read + write (like the real benchmark)
        var readTask = Task.Run(async () =>
        {
            var readSw = Stopwatch.StartNew();
            var recvBuffer = new byte[chunkSize];
            long totalRead = 0;
            int readCount = 0;
            while (totalRead < dataSize)
            {
                var iterSw = Stopwatch.StartNew();
                var read = await readChannel!.ReadAsync(recvBuffer, cts.Token);
                var iterTime = iterSw.Elapsed;
                if (read == 0) break;
                totalRead += read;
                readCount++;
                Console.Error.WriteLine($"    ReadAsync #{readCount}: {read} bytes in {iterTime.TotalMilliseconds:F2}ms");
            }
            Console.Error.WriteLine($"  Total reads: {readCount}, {readSw.Elapsed.TotalMilliseconds:F2}ms");
        }, cts.Token);

        var writeTask = Task.Run(async () =>
        {
            var writeSw = Stopwatch.StartNew();
            long totalSent = 0;
            int writeCount = 0;
            while (totalSent < dataSize)
            {
                var toSend = (int)Math.Min(chunkSize, dataSize - totalSent);
                var iterSw = Stopwatch.StartNew();
                await writeChannel.WriteAsync(sendBuffer.AsMemory(0, toSend), cts.Token);
                var iterTime = iterSw.Elapsed;
                totalSent += toSend;
                writeCount++;
                Console.Error.WriteLine($"    WriteAsync #{writeCount}: {toSend} bytes in {iterTime.TotalMilliseconds:F2}ms");
            }
            Console.Error.WriteLine($"  Total writes: {writeCount}, {writeSw.Elapsed.TotalMilliseconds:F2}ms");
            await writeChannel.FlushAsync(cts.Token);
            var closeSw = Stopwatch.StartNew();
            await writeChannel.CloseAsync(cts.Token);
            Console.Error.WriteLine($"  CloseAsync: {closeSw.Elapsed.TotalMilliseconds:F2}ms");
        }, cts.Token);

        await Task.WhenAll(readTask, writeTask);

        overallSw.Stop();
        var throughput = dataSize / overallSw.Elapsed.TotalSeconds / 1_048_576;
        Console.Error.WriteLine($"  Overall: {overallSw.Elapsed.TotalMilliseconds:F2}ms, {throughput:F2} MB/s");

        await writeChannel.DisposeAsync();
        await readChannel!.DisposeAsync();
        await server.DisposeAsync();
        await client.DisposeAsync();
        listener.Stop();
    }
}
