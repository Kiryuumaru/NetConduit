using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using NetConduit;
using NetConduit.Udp;

namespace NetConduit.Benchmarks;

/// <summary>
/// UDP throughput benchmarks comparing raw UDP vs multiplexed UDP.
/// Note: UDP multiplexer uses reliable delivery shim, adding overhead for reliability.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class UdpThroughputBenchmark
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

    [Params(1, 10, 50)]
    public int ConcurrentChannels { get; set; }

    [Params(1_024, 10_240, 102_400)]  // 1KB, 10KB, 100KB per channel (smaller for UDP)
    public int DataSizePerChannel { get; set; }

    private byte[] _sendBuffer = null!;
    private const int ChunkSize = 1400; // Safe UDP payload size

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sendBuffer = new byte[Math.Min(DataSizePerChannel, ChunkSize)];
        Random.Shared.NextBytes(_sendBuffer);
    }

    /// <summary>
    /// Multiplexed UDP throughput - 1 connection with N channels, each transferring DataSizePerChannel.
    /// Uses reliable UDP shim for ordered delivery.
    /// </summary>
    [Benchmark(Description = "Mux UDP Throughput")]
    public async Task MuxUdp_Throughput()
    {
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var serverUdp = new UdpClient(serverEndpoint);
        var actualServerPort = ((IPEndPoint)serverUdp.Client.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var muxOptions = new MultiplexerOptions
        {
            EnableReconnection = false,
            FlushMode = FlushMode.Immediate
        };

        var serverTask = Task.Run(async () =>
        {
            await using var server = await UdpMultiplexer.AcceptAsync(actualServerPort, null, muxOptions, cts.Token);
            var runTask = await server.StartAsync(cts.Token);

            var acceptedChannels = new List<ReadChannel>();
            var readTasks = new List<Task>();

            await foreach (var channel in server.Multiplexer.AcceptChannelsAsync(cts.Token))
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
            await using var client = await UdpMultiplexer.ConnectAsync("127.0.0.1", actualServerPort, null, muxOptions, cts.Token);
            var runTask = await client.StartAsync(cts.Token);

            var sendTasks = new List<Task>();
            var channels = new List<WriteChannel>();

            for (int i = 0; i < ConcurrentChannels; i++)
            {
                var channelId = $"ch-{i}";
                var channel = await client.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
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
}
