using Application.Common.Extensions;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Services;
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

    class WaiterCount
    {
        public int Count { get; set; }
    }

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

        Task.Run(async () =>
        {
            await Task.Delay(5000);
            streamPipelineService.Start().Forget();
        }).Forget();

        WaiterCount waiterCount = new();

        List<Task> moqs = [];
        for (int i = 0; i < EdgeDefaults.RawMockChannelCount; i++)
        {
            var moqChannel = new Guid($"00000000-0000-0000-0000-{(EdgeDefaults.RawMockChannelKeyOffset + i):D12}");
            moqs.Add(StartMockStreamRaw(moqChannel, streamPipelineService, waiterCount, tcpHost, tcpPort, cts.Token));
        }
        for (int i = 0; i < EdgeDefaults.MsgMockChannelCount; i++)
        {
            var moqChannel = new Guid($"00000000-0000-0000-0000-{(EdgeDefaults.MsgMockChannelKeyOffset + i):D12}");
            moqs.Add(StartMockStreamMessaging(moqChannel, streamPipelineService, waiterCount, cts.Token));
        }

        return Task.WhenAll(moqs);
    }

    ConcurrentDictionary<Guid, (MockPayload payload, Stopwatch stopwatch)> msgStreamMapMock = [];
    TimeSpan msgStreamLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset msgStreamLastLog = DateTimeOffset.MinValue;
    ConcurrentDictionary<Guid, List<double>> msgStreamAveLi = [];
    private Task StartMockStreamMessaging(Guid channelKey, StreamPipelineService streamPipelineService, WaiterCount waiterCount, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(channelKey, $"MOOCK-{channelKey}");

        mockStream.OnMessage(async payload =>
        {
            await Task.Delay(100);

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

            var aveList = msgStreamAveLi.GetOrAdd(channelKey, []);
            while (aveList.Count > EdgeDefaults.MockAveCount)
            {
                aveList.RemoveAt(0);
            }

            aveList.Add(mock.stopwatch.ElapsedMilliSeconds());
            msgStreamMapMock.Remove(clientPayload.MessageGuid, out _);

            if (msgStreamLastLog + msgStreamLogSpan < DateTimeOffset.UtcNow)
            {
                msgStreamLastLog = DateTimeOffset.UtcNow;
                _logger.LogInformation("Messaging mock time {TimeStamp:0.###}ms, map count: {MapCount}", msgStreamAveLi.Values.SelectMany(i => i).Average(), msgStreamMapMock.Count);
            }
        });

        waiterCount.Count++;

        return Task.Run(async () =>
        {
            await Task.Delay(5000);

            _logger.LogInformation("Mock messaging pipe started");

            waiterCount.Count--;
            while (waiterCount.Count != 0) { await Task.Delay(100); }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string mockVal = RandomHelpers.Alphanumeric(1000);
                    var payload = new MockPayload() { MockMessage = mockVal };
                    var stopwatch = Stopwatch.StartNew();
                    var guid = mockStream.Send(payload);

                    msgStreamMapMock[guid] = (payload, stopwatch);

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

    TimeSpan mockStreamRawLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset mockStreamRawLastLog = DateTimeOffset.MinValue;
    ConcurrentDictionary<Guid, List<double>> mockStreamRawAveLi = [];
    SemaphoreSlim aveLocker = new(1);
    private Task StartMockStreamRaw(Guid channelKey, StreamPipelineService streamPipelineService, WaiterCount waiterCount, string tcpHost, int tcpPort, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetRaw(channelKey, EdgeDefaults.EdgeCommsBufferSize);

        _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} started", tcpHost, tcpPort);

        waiterCount.Count++;

        return Task.Run(async () =>
        {   
            await Task.Delay(5000);

            _logger.LogInformation("Mock raw bytes started");

            waiterCount.Count--;
            while (waiterCount.Count != 0) { await Task.Delay(100); }

            Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
            {
                var ict = stoppingToken.WithTimeout(TimeSpan.FromMinutes(99));

                //string sendStr = RandomHelpers.Alphanumeric(101);
                string sendStr = RandomHelpers.Alphanumeric(10001);
                //string sendStr = RandomHelpers.Alphanumeric(Random.Shared.Next(10000));
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    var sw = Stopwatch.StartNew();

                    await mockStream.WriteAsync(sendBytes, ict);

                    var writeMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    var bytesRead = await mockStream.ReadAsync(receivedBytes, ict);

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
                        try
                        {
                            await aveLocker.WaitAsync(stoppingToken);

                            var aveList = mockStreamRawAveLi.GetOrAdd(channelKey, []);
                            aveList.Add(writeMs + readMs);
                            while (aveList.Count > EdgeDefaults.MockAveCount)
                            {
                                aveList.RemoveAt(0);
                            }
                            if (mockStreamRawLastLog + mockStreamRawLogSpan < DateTimeOffset.UtcNow)
                            {
                                mockStreamRawLastLog = DateTimeOffset.UtcNow;
                                _logger.LogInformation("Raw bytes received time {TimeStamp:0.###}ms", mockStreamRawAveLi.Values.SelectMany(i => i).Average());
                            }
                        }
                        finally
                        {
                            aveLocker.Release();
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
