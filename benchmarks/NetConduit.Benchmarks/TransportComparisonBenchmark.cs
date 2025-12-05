using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NetConduit;
using NetConduit.Ipc;
using NetConduit.Tcp;
using NetConduit.Udp;
using NetConduit.WebSocket;

namespace NetConduit.Benchmarks;

/// <summary>
/// Cross-transport comparison benchmark: Compare all transports under identical workload.
/// Helps identify which transport is best suited for different use cases.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class TransportComparisonBenchmark
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun
                .WithLaunchCount(1)
                .WithWarmupCount(1)
                .WithIterationCount(3)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
            AddColumn(StatisticColumn.Mean);
            AddColumn(StatisticColumn.StdDev);
        }
    }

    [Params(10)]
    public int ConcurrentChannels { get; set; }

    [Params(102_400)]  // 100KB per channel - balanced workload
    public int DataSizePerChannel { get; set; }

    private byte[] _sendBuffer = null!;
    private const int ChunkSize = 64 * 1024;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sendBuffer = new byte[Math.Min(DataSizePerChannel, ChunkSize)];
        Random.Shared.NextBytes(_sendBuffer);
    }

    /// <summary>
    /// TCP multiplexer - reliable, ordered, connection-oriented.
    /// Best for: General purpose, WAN connections, firewall-friendly.
    /// </summary>
    [Benchmark(Baseline = true, Description = "TCP Mux")]
    public async Task TcpMux()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var muxOptions = new MultiplexerOptions
        {
            EnableReconnection = false,
            FlushMode = FlushMode.Immediate
        };

        try
        {
            var serverTask = Task.Run(async () =>
            {
                await using var server = await TcpMultiplexer.AcceptAsync(listener, muxOptions, cts.Token);
                var runTask = await server.StartAsync(cts.Token);
                await AcceptAndReadChannels(server.Multiplexer, ConcurrentChannels, DataSizePerChannel, ChunkSize, cts.Token);
            }, cts.Token);

            var clientTask = Task.Run(async () =>
            {
                await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port, muxOptions, cts.Token);
                var runTask = await client.StartAsync(cts.Token);
                await OpenAndWriteChannels(client.Multiplexer, ConcurrentChannels, DataSizePerChannel, _sendBuffer, cts.Token);
            }, cts.Token);

            await Task.WhenAll(serverTask, clientTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// UDP multiplexer with reliable delivery shim.
    /// Best for: Low latency, real-time applications, NAT traversal.
    /// </summary>
    [Benchmark(Description = "UDP Mux")]
    public async Task UdpMux()
    {
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var serverUdp = new UdpClient(serverEndpoint);
        var actualServerPort = ((IPEndPoint)serverUdp.Client.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var muxOptions = new MultiplexerOptions
        {
            EnableReconnection = false,
            FlushMode = FlushMode.Immediate
        };

        var serverTask = Task.Run(async () =>
        {
            await using var server = await UdpMultiplexer.AcceptAsync(actualServerPort, null, muxOptions, cts.Token);
            var runTask = await server.StartAsync(cts.Token);
            await AcceptAndReadChannels(server.Multiplexer, ConcurrentChannels, DataSizePerChannel, ChunkSize, cts.Token);
        }, cts.Token);

        var clientTask = Task.Run(async () =>
        {
            await using var client = await UdpMultiplexer.ConnectAsync("127.0.0.1", actualServerPort, null, muxOptions, cts.Token);
            var runTask = await client.StartAsync(cts.Token);
            await OpenAndWriteChannels(client.Multiplexer, ConcurrentChannels, DataSizePerChannel, _sendBuffer, cts.Token);
        }, cts.Token);

        await Task.WhenAll(serverTask, clientTask);
    }

    /// <summary>
    /// IPC multiplexer - local machine only, lowest latency.
    /// Best for: Inter-process communication, microservices on same host.
    /// </summary>
    [Benchmark(Description = "IPC Mux")]
    public async Task IpcMux()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var muxOptions = new MultiplexerOptions
        {
            EnableReconnection = false,
            FlushMode = FlushMode.Immediate
        };

        var pipeName = $"netconduit_bench_{Guid.NewGuid():N}";

        var serverTask = Task.Run(async () =>
        {
            await using var server = await IpcMultiplexer.AcceptAsync(pipeName, muxOptions, cts.Token);
            var runTask = await server.StartAsync(cts.Token);
            await AcceptAndReadChannels(server.Multiplexer, ConcurrentChannels, DataSizePerChannel, ChunkSize, cts.Token);
        }, cts.Token);

        await Task.Delay(100, cts.Token); // Ensure server is listening

        var clientTask = Task.Run(async () =>
        {
            await using var client = await IpcMultiplexer.ConnectAsync(pipeName, muxOptions, cts.Token);
            var runTask = await client.StartAsync(cts.Token);
            await OpenAndWriteChannels(client.Multiplexer, ConcurrentChannels, DataSizePerChannel, _sendBuffer, cts.Token);
        }, cts.Token);

        await Task.WhenAll(serverTask, clientTask);
    }

    /// <summary>
    /// WebSocket multiplexer - HTTP-compatible, proxy-friendly.
    /// Best for: Web applications, cloud environments, HTTP/2 proxies.
    /// </summary>
    [Benchmark(Description = "WebSocket Mux")]
    public async Task WebSocketMux()
    {
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var muxOptions = new MultiplexerOptions
        {
            EnableReconnection = false,
            FlushMode = FlushMode.Immediate
        };

        WebSocketMultiplexerConnection? serverConnection = null;
        var serverReady = new TaskCompletionSource<WebSocketMultiplexerConnection>();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                serverConnection = WebSocketMultiplexer.Accept(ws, muxOptions);
                serverReady.TrySetResult(serverConnection);
                try { await Task.Delay(Timeout.Infinite, cts.Token); }
                catch (OperationCanceledException) { }
            }
        });

        await app.StartAsync(cts.Token);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                var server = await serverReady.Task;
                var runTask = await server.StartAsync(cts.Token);
                await AcceptAndReadChannels(server.Multiplexer, ConcurrentChannels, DataSizePerChannel, ChunkSize, cts.Token);
            }, cts.Token);

            var clientTask = Task.Run(async () =>
            {
                await using var client = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws", muxOptions, null, cts.Token);
                var runTask = await client.StartAsync(cts.Token);
                await OpenAndWriteChannels(client.Multiplexer, ConcurrentChannels, DataSizePerChannel, _sendBuffer, cts.Token);
            }, cts.Token);

            await Task.WhenAll(serverTask, clientTask);
        }
        finally
        {
            await cts.CancelAsync();
            await app.StopAsync();
            if (serverConnection != null)
                await serverConnection.DisposeAsync();
        }
    }

    private static async Task AcceptAndReadChannels(StreamMultiplexer mux, int channelCount, int dataSize, int chunkSize, CancellationToken ct)
    {
        var acceptedChannels = new List<ReadChannel>();
        var readTasks = new List<Task>();

        await foreach (var channel in mux.AcceptChannelsAsync(ct))
        {
            acceptedChannels.Add(channel);
            var ch = channel;
            readTasks.Add(Task.Run(async () =>
            {
                var buffer = new byte[chunkSize];
                long totalRead = 0;
                while (totalRead < dataSize)
                {
                    var read = await ch.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    totalRead += read;
                }
            }, ct));

            if (acceptedChannels.Count >= channelCount) break;
        }

        await Task.WhenAll(readTasks);

        foreach (var ch in acceptedChannels)
            await ch.DisposeAsync();
    }

    private static async Task OpenAndWriteChannels(StreamMultiplexer mux, int channelCount, int dataSize, byte[] sendBuffer, CancellationToken ct)
    {
        var channels = new List<WriteChannel>();
        var sendTasks = new List<Task>();

        for (int i = 0; i < channelCount; i++)
        {
            var channelId = $"ch-{i}";
            var channel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, ct);
            channels.Add(channel);

            var ch = channel;
            sendTasks.Add(Task.Run(async () =>
            {
                long totalSent = 0;
                while (totalSent < dataSize)
                {
                    var toSend = (int)Math.Min(sendBuffer.Length, dataSize - totalSent);
                    await ch.WriteAsync(sendBuffer.AsMemory(0, toSend), ct);
                    totalSent += toSend;
                }
                await ch.FlushAsync(ct);
                await ch.CloseAsync(ct);
            }, ct));
        }

        await Task.WhenAll(sendTasks);

        foreach (var ch in channels)
            await ch.DisposeAsync();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
