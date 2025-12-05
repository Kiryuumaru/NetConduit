using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using NetConduit;
using NetConduit.Tcp;

namespace NetConduit.Benchmarks;

/// <summary>
/// Throughput benchmarks measuring data transfer rate with multiplexing overhead.
/// Tests channel count × data size matrix for throughput comparison.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class ThroughputBenchmark
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

    [Params(1, 10, 100, 1000)]
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
    /// Raw TCP throughput - N connections, each transferring DataSizePerChannel
    /// </summary>
    [Benchmark(Baseline = true, Description = "Raw TCP Throughput")]
    public async Task RawTcp_Throughput()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            var serverTask = Task.Run(async () =>
            {
                var tasks = new List<Task>();
                for (int i = 0; i < ConcurrentChannels; i++)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using var stream = client.GetStream();
                            var buffer = new byte[ChunkSize];
                            long totalRead = 0;
                            while (totalRead < DataSizePerChannel)
                            {
                                var read = await stream.ReadAsync(buffer, cts.Token);
                                if (read == 0) break;
                                totalRead += read;
                            }
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, cts.Token));
                }
                await Task.WhenAll(tasks);
            }, cts.Token);

            var clientTasks = new List<Task>();
            for (int i = 0; i < ConcurrentChannels; i++)
            {
                clientTasks.Add(Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", port, cts.Token);
                    using var stream = client.GetStream();

                    long totalSent = 0;
                    while (totalSent < DataSizePerChannel)
                    {
                        var toSend = (int)Math.Min(_sendBuffer.Length, DataSizePerChannel - totalSent);
                        await stream.WriteAsync(_sendBuffer.AsMemory(0, toSend), cts.Token);
                        totalSent += toSend;
                    }
                }, cts.Token));
            }

            await Task.WhenAll(clientTasks);
            await serverTask;
        }
        finally
        {
            listener.Stop();
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Multiplexed TCP throughput - 1 connection with N channels, each transferring DataSizePerChannel
    /// </summary>
    [Benchmark(Description = "Mux TCP Throughput")]
    public async Task MuxTcp_Throughput()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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
                await using var client = await TcpMultiplexer.ConnectAsync("127.0.0.1", port, muxOptions, cts.Token);
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
            listener.Stop();
            await Task.Delay(50);
        }
    }
}
