using System.Net;
using System.Net.WebSockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NetConduit;
using NetConduit.WebSocket;

namespace NetConduit.Benchmarks;

/// <summary>
/// WebSocket throughput benchmarks comparing raw WebSocket vs multiplexed WebSocket.
/// Tests channel count × data size matrix for throughput comparison over WebSocket transport.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class WebSocketThroughputBenchmark
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

    [Params(1, 10, 100)]
    public int ConcurrentChannels { get; set; }

    [Params(1_024, 102_400, 1_048_576)]  // 1KB, 100KB, 1MB per channel
    public int DataSizePerChannel { get; set; }

    private byte[] _sendBuffer = null!;
    private const int ChunkSize = 64 * 1024; // 64 KB chunks

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sendBuffer = new byte[Math.Min(DataSizePerChannel, ChunkSize)];
        Random.Shared.NextBytes(_sendBuffer);
    }

    /// <summary>
    /// Raw WebSocket throughput - N connections, each transferring DataSizePerChannel.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Raw WebSocket")]
    public async Task RawWebSocket_Throughput()
    {
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var connectedSockets = new List<System.Net.WebSockets.WebSocket>();
        var connectLock = new SemaphoreSlim(1);
        var allConnected = new TaskCompletionSource();
        var connectionCount = 0;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                await connectLock.WaitAsync(cts.Token);
                try
                {
                    connectedSockets.Add(ws);
                    connectionCount++;
                    if (connectionCount >= ConcurrentChannels)
                        allConnected.TrySetResult();
                }
                finally
                {
                    connectLock.Release();
                }

                // Read all data
                var buffer = new byte[ChunkSize];
                long totalRead = 0;
                while (totalRead < DataSizePerChannel)
                {
                    var result = await ws.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    totalRead += result.Count;
                }
            }
        });

        await app.StartAsync(cts.Token);

        try
        {
            // Connect all clients
            var clientTasks = new List<Task>();
            var clientSockets = new List<ClientWebSocket>();

            for (int i = 0; i < ConcurrentChannels; i++)
            {
                var clientWs = new ClientWebSocket();
                clientSockets.Add(clientWs);
                clientTasks.Add(Task.Run(async () =>
                {
                    await clientWs.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), cts.Token);

                    long totalSent = 0;
                    while (totalSent < DataSizePerChannel)
                    {
                        var toSend = (int)Math.Min(_sendBuffer.Length, DataSizePerChannel - totalSent);
                        await clientWs.SendAsync(_sendBuffer.AsMemory(0, toSend), WebSocketMessageType.Binary, false, cts.Token);
                        totalSent += toSend;
                    }

                    await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }, cts.Token));
            }

            await Task.WhenAll(clientTasks);

            foreach (var ws in clientSockets)
                ws.Dispose();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// Multiplexed WebSocket throughput - 1 connection with N channels, each transferring DataSizePerChannel.
    /// </summary>
    [Benchmark(Description = "Mux WebSocket")]
    public async Task MuxWebSocket_Throughput()
    {
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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

                // Keep connection alive until done
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
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

                var acceptedChannels = new List<ReadChannel>();
                var readTasks = new List<Task>();

                await foreach (var channel in server.AcceptChannelsAsync(cts.Token))
                {
                    acceptedChannels.Add(channel);
                    var ch = channel;
                    readTasks.Add(Task.Run(async () =>
                    {
                        var buffer = new byte[ChunkSize];
                        long totalRead = 0;
                        while (totalRead < DataSizePerChannel)
                        {
                            var read = await ch.ReadAsync(buffer, cts.Token);
                            if (read == 0) break;
                            totalRead += read;
                        }
                    }, cts.Token));

                    if (acceptedChannels.Count >= ConcurrentChannels) break;
                }

                await Task.WhenAll(readTasks);

                foreach (var ch in acceptedChannels)
                {
                    await ch.DisposeAsync();
                }
            }, cts.Token);

            var clientTask = Task.Run(async () =>
            {
                await using var client = await WebSocketMultiplexer.ConnectAsync($"ws://localhost:{port}/ws", muxOptions, null, cts.Token);
                var runTask = await client.StartAsync(cts.Token);

                var sendTasks = new List<Task>();
                var channels = new List<WriteChannel>();

                for (int i = 0; i < ConcurrentChannels; i++)
                {
                    var channelId = $"ch-{i}";
                    var channel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                    channels.Add(channel);

                    var ch = channel;
                    sendTasks.Add(Task.Run(async () =>
                    {
                        long totalSent = 0;
                        while (totalSent < DataSizePerChannel)
                        {
                            var toSend = (int)Math.Min(_sendBuffer.Length, DataSizePerChannel - totalSent);
                            await ch.WriteAsync(_sendBuffer.AsMemory(0, toSend), cts.Token);
                            totalSent += toSend;
                        }
                        await ch.FlushAsync(cts.Token);
                        await ch.CloseAsync(cts.Token);
                    }, cts.Token));
                }

                await Task.WhenAll(sendTasks);

                foreach (var ch in channels)
                {
                    await ch.DisposeAsync();
                }
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

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
