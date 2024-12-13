using Application.Common.Extensions;
using Application.Common.Features;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Features;
using Application.StreamPipeline.Services;
using Application.StreamPipeline.Services.Pipes;
using Application.Tcp.Services;
using Domain.Edge.Models;
using Domain.StreamPipeline.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Application.Edge.Mockers;

internal class EdgeClientMockWorker(ILogger<EdgeClientMockWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeClientMockWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeClientMockWorker), nameof(ExecuteAsync));

        using var scope = _serviceProvider.CreateScope();
        var edgeLocalService = scope.ServiceProvider.GetRequiredService<IEdgeLocalStoreService>();
        var edgeWorkerStartedService = scope.ServiceProvider.GetRequiredService<EdgeWorkerStartedService>();

        await edgeWorkerStartedService.WaitForOpen(stoppingToken);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        using var _ = _logger.BeginScopeMap(nameof(EdgeClientMockWorker), nameof(Routine), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        using var scope = _serviceProvider.CreateScope();
        var tcpClient = scope.ServiceProvider.GetRequiredService<TcpClientService>();

        await Task.Delay(5000, stoppingToken);

        await tcpClient.Start(tcpHost, tcpPort, (tranceiverStream, ct) =>
        {
            var clientCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                ct.Token,
                tranceiverStream.CancelWhenDisposing());

            return Start(tranceiverStream, tcpHost, tcpPort, clientCts);

        }, stoppingToken);
    }

    private Task Start(TranceiverStream tranceiverStream, string tcpHost, int tcpPort, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeClientMockWorker), nameof(Start), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        var scope = _serviceProvider.CreateScope();

        cts.Token.Register(scope.Dispose);

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} started", tcpHost, tcpPort); },
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} ended", tcpHost, tcpPort); },
            ex => { _logger.LogError("Stream multiplexer {ServerHost}:{ServerPort} error: {Error}", tcpHost, tcpPort, ex.Message); },
            cts.Token);

        ConcurrentDictionary<Guid, List<double>> mockStreamRawAveLi1 = [];

        ConcurrentDictionary<Guid, (MockPayload payload, Stopwatch stopwatch)> msgStreamMapMock = [];
        ConcurrentDictionary<Guid, List<double>> msgStreamAveLi = [];

        int channelCount = 0;
        int channelIndex = EdgeDefaults.MockChannelKeyOffset;
        Guid NextChannel()
        {
            channelCount++;
            return new Guid($"00000000-0000-0000-0000-{channelIndex++:D12}");
        }

        Stopwatch loadingSw = Stopwatch.StartNew();

        GateKeeperCounter waiterCount = new();

        List<Func<Task>> moqs = [];
        moqs.Add(async () =>
        {
            await waiterCount.WaitForOpen(cts.Token);
            streamPipelineService.Start().Forget();
            _logger.LogInformation("Loading time {LoadingTimeMs}ms for {ChannelCount} channels", loadingSw.ElapsedMilliSeconds(), channelCount);
        });
        moqs.Add(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                bool ready = true;

                double rawAve1 = 0;
                var rawTimeEnum = mockStreamRawAveLi1.Values.SelectMany(i => i);
                if (rawTimeEnum.Any())
                {
                    rawAve1 = rawTimeEnum.Average();
                }
                else if (EdgeDefaults.RawMockChannelCount != 0)
                {
                    ready = false;
                }

                double msgAve1 = 0;
                var msgTimeEnum = msgStreamAveLi.Values.SelectMany(i => i);
                if (msgTimeEnum.Any())
                {
                    msgAve1 = msgTimeEnum.Average();
                }
                else if (EdgeDefaults.MsgMockChannelCount != 0)
                {
                    ready = false;
                }

                if (ready)
                {
                    _logger.LogInformation("Raw: {RawTimeStamp:0.000}ms, Msg: {MsgTimeStamp:0.000}ms/{MapCount}", rawAve1, msgAve1, msgStreamMapMock.Count);
                }

                await Task.Delay(1000);
            }
        });
        for (int i = 0; i < EdgeDefaults.RawMockChannelCount; i++)
        {
            var moqChannel = NextChannel();
            TranceiverStream mockStream = new(
                new BlockingMemoryStream(EdgeDefaults.EdgeCommsBufferSize),
                new BlockingMemoryStream(EdgeDefaults.EdgeCommsBufferSize));
            streamPipelineService.SetRaw(moqChannel, mockStream);
            var aveList = mockStreamRawAveLi1.GetOrAdd(moqChannel, []);
            moqs.Add(() => StartMockStreamRaw(aveList, mockStream, waiterCount, tcpHost, tcpPort, cts.Token));
        }
        for (int i = 0; i < EdgeDefaults.MsgMockChannelCount; i++)
        {
            var moqChannel = NextChannel();
            var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(moqChannel, $"MOOCK-{moqChannel}");
            var aveList = msgStreamAveLi.GetOrAdd(moqChannel, []);
            moqs.Add(() => StartMockStreamMessaging(msgStreamMapMock, aveList, moqChannel, mockStream, waiterCount, cts.Token));
        }

        return Task.WhenAll(moqs.Select(i => i()));
    }

    private Task StartMockStreamMessaging(ConcurrentDictionary<Guid, (MockPayload payload, Stopwatch stopwatch)> msgStreamMapMock, List<double> aveList, Guid channelKey, MessagingPipe<MockPayload, MockPayload> mockStream, GateKeeperCounter waiterCount, CancellationToken stoppingToken)
    {
        mockStream.OnMessage(payload =>
        {
            if (JsonSerializer.Deserialize<MessagingPipePayload<MockPayload>>(payload.Message.MockMessage) is not MessagingPipePayload<MockPayload> clientPayload)
            {
                _logger.LogError("Invalid payload received {PayloadMessageGuid}", payload.MessageGuid);
                return;
            }
            if (!msgStreamMapMock.TryGetValue(clientPayload.MessageGuid, out var mock))
            {
                _logger.LogError("Unknown payload received {PayloadMessageGuid}", clientPayload.MessageGuid);
                return;
            }
            if (mock.payload.MockMessage != clientPayload.Message.MockMessage)
            {
                _logger.LogError("Mismatch payload received value {PayloadMessageGuid}", payload.MessageGuid);
            }

            mock.stopwatch.Stop();

            aveList.Add(mock.stopwatch.ElapsedMilliSeconds());
            while (aveList.Count > EdgeDefaults.MockAveCount)
            {
                aveList.RemoveAt(0);
            }

            msgStreamMapMock.Remove(clientPayload.MessageGuid, out _);
        });

        waiterCount.Increment();

        return Task.Run(async () =>
        {
            await Task.Delay(1000);

            waiterCount.Decrement();
            await waiterCount.WaitForOpen(stoppingToken);

            _logger.LogInformation("Mock messaging pipe started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string mockVal = RandomHelpers.Alphanumeric(1000);
                    var payload = new MockPayload() { MockMessage = mockVal };
                    var stopwatch = Stopwatch.StartNew();
                    var guid = Guid.NewGuid();

                    msgStreamMapMock[guid] = (payload, stopwatch);

                    await mockStream.Send(guid, payload);

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error MessagingPipe {ChannelKey}: {Error}", channelKey, ex.Message);
                }
            }

        }, stoppingToken);
    }

    private Task StartMockStreamRaw(List<double> aveList, TranceiverStream mockStream, GateKeeperCounter waiterCount, string tcpHost, int tcpPort, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} started", tcpHost, tcpPort);

        waiterCount.Increment();

        return Task.Run(async () =>
        {
            await Task.Delay(1000);

            waiterCount.Decrement();
            await waiterCount.WaitForOpen(stoppingToken);

            _logger.LogInformation("Mock raw bytes started");

            Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested)
            {
                var ict = stoppingToken.WithTimeout(TimeSpan.FromMinutes(3));

                //string sendStr = RandomHelpers.Alphanumeric(101);
                string sendStr = RandomHelpers.Alphanumeric(10001);
                //string sendStr = RandomHelpers.Alphanumeric(Random.Shared.Next(10000));
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    var sw = Stopwatch.StartNew();

                    mockStream.Write(sendBytes.AsSpan());

                    var writeMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    var bytesRead = await mockStream.ReadAsync(receivedBytes, ict);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var readMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    //_logger.LogInformation("Raw bytes: S {Write:0.00}ms, R {Read:0.00}ms, T {Total:0.00}ms", writeMs, readMs, writeMs + readMs);

                    string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesRead].Span);

                    if (sendStr != receivedStr)
                    {
                        _logger.LogError("Mismatch: {Sent} bytes != {Received} bytes", sendStr.Length, receivedStr.Length);
                    }
                    else
                    {
                        aveList.Add(writeMs + readMs);
                        while (aveList.Count > EdgeDefaults.MockAveCount)
                        {
                            aveList.RemoveAt(0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {ServerHost}:{ServerPort}: {Error}", tcpHost, tcpPort, ex.Message);
                }

                await Task.Delay(100, stoppingToken);
            }

            _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} ended", tcpHost, tcpPort);

        }, stoppingToken);
    }
}
