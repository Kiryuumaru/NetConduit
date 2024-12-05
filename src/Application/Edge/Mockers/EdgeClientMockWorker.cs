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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(ExecuteAsync));

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

        return Task.WhenAll(
            StartMockStreamRaw(EdgeDefaults.MockChannelKey0, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey1, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey2, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey3, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey4, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey5, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey6, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey7, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey8, streamPipelineService, tcpHost, tcpPort, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey9, streamPipelineService, tcpHost, tcpPort, cts.Token)
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey0, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey1, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey2, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey3, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey4, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey5, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey6, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey7, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey8, streamPipelineService, cts.Token),
            //StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey9, streamPipelineService, cts.Token)
            );
    }

    int msgStreamAveLent = 10;
    ConcurrentDictionary<Guid, (MockPayload payload, Stopwatch stopwatch)> msgStreamMapMock = [];
    TimeSpan msgStreamLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset msgStreamLastLog = DateTimeOffset.MinValue;
    List<double> msgStreamAveLi = [];
    private Task StartMockStreamMessaging(Guid channelKey, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
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

            while (msgStreamAveLi.Count > msgStreamAveLent)
            {
                msgStreamAveLi.RemoveAt(0);
            }

            mock.stopwatch.Stop();
            msgStreamAveLi.Add(mock.stopwatch.ElapsedMilliseconds);
            msgStreamMapMock.Remove(clientPayload.MessageGuid, out _);

            if (msgStreamLastLog + msgStreamLogSpan < DateTimeOffset.UtcNow)
            {
                msgStreamLastLog = DateTimeOffset.UtcNow;
                _logger.LogInformation("Messaging mock time {TimeStamp:0.###}ms, map count: {MapCount}", msgStreamAveLi.Average(), msgStreamMapMock.Count);
            }
        });

        return Task.Run(async () =>
        {
            await Task.Delay(5000);

            _logger.LogInformation("Mock messaging pipe started");

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

    int mockStreamRawAveLent = 10;
    TimeSpan mockStreamRawLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset mockStreamRawLastLog = DateTimeOffset.MinValue;
    List<double> mockStreamRawAveLi = [];
    SemaphoreSlim aveLocker = new(1);
    private Task StartMockStreamRaw(Guid channelKey, StreamPipelineService streamPipelineService, string tcpHost, int tcpPort, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetRaw(channelKey, EdgeDefaults.EdgeCommsBufferSize);

        _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} started", tcpHost, tcpPort);

        return Task.Run(async () =>
        {
            await Task.Delay(5000);

            _logger.LogInformation("Mock raw bytes started");

            Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
            {
                var ict = stoppingToken.WithTimeout(TimeSpan.FromMinutes(5));

                string sendStr = RandomHelpers.Alphanumeric(101);
                //string sendStr = RandomHelpers.Alphanumeric(10001);
                //string sendStr = RandomHelpers.Alphanumeric(Random.Shared.Next(10000));
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    var writeStopwatch = Stopwatch.StartNew();

                    await mockStream.WriteAsync(sendBytes, ict);

                    writeStopwatch.Stop();
                    var writeMs = writeStopwatch.ElapsedMilliseconds;

                    var readStopwatch = Stopwatch.StartNew();

                    var bytesRead = await mockStream.ReadAsync(receivedBytes, ict);

                    readStopwatch.Stop();
                    var readMs = readStopwatch.ElapsedMilliseconds;

                    //_logger.LogInformation("Raw bytes: S {TimeStamp:0.###}ms, R {TimeStamp:0.###}ms, T {TimeStamp:0.###}ms",
                    //    writeMs, readMs, writeMs + readMs);

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

                            mockStreamRawAveLi.Add(writeMs + readMs);
                            while (mockStreamRawAveLi.Count > mockStreamRawAveLent)
                            {
                                mockStreamRawAveLi.RemoveAt(0);
                            }
                            if (mockStreamRawLastLog + mockStreamRawLogSpan < DateTimeOffset.UtcNow)
                            {
                                mockStreamRawLastLog = DateTimeOffset.UtcNow;
                                _logger.LogInformation("Raw bytes received time {TimeStamp:0.###}ms", mockStreamRawAveLi.Average());
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
