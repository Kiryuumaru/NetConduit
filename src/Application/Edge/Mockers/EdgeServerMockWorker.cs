using Application.Common.Extensions;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Application.Edge.Mockers;

internal class EdgeServerMockWorker(ILogger<EdgeServerMockWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeServerMockWorker> _logger = logger;
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
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpServer = scope.ServiceProvider.GetRequiredService<TcpServerService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpServer.Start(tcpHost, tcpPort, (tcpClient, tranceiverStream, ct) =>
        {
            var clientCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                ct.Token,
                tranceiverStream.CancelWhenDisposing());

            IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;

            return Start(clientEndPoint, tranceiverStream, clientCts);

        }, stoppingToken);
    }

    private Task Start(IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(Start), new()
        {
            ["ClientAddress"] = iPAddress
        });

        var scope = _serviceProvider.CreateScope();

        cts.Token.Register(scope.Dispose);

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress); },
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress); },
            ex => { _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message); },
            cts.Token);

        Task.Run(async () =>
        {
            await Task.Delay(5000);
            streamPipelineService.Start().Forget();
        }).Forget();

        return Task.WhenAll(
            StartMockStreamRaw(EdgeDefaults.MockChannelKey0, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey1, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey2, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey3, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey4, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey5, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey6, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey7, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey8, streamPipelineService, iPAddress, tranceiverStream, cts.Token),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey9, streamPipelineService, iPAddress, tranceiverStream, cts.Token)
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

    private Task StartMockStreamMessaging(Guid channelKey, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(channelKey, $"MOOCK-{channelKey}");

        stoppingToken.Register(mockStream.Dispose);

        mockStream.OnMessage(payload =>
        {
            mockStream.Send(new()
            {
                MockMessage = JsonSerializer.Serialize(payload)
            });
        });

        return Task.CompletedTask;
    }

    private Task StartMockStreamRaw(Guid channelKey, StreamPipelineService streamPipelineService, IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetRaw(channelKey, EdgeDefaults.EdgeCommsBufferSize);

        _logger.LogInformation("Stream pipe {ClientAddress} started", iPAddress);

        return Task.Run(() =>
        {
            Span<byte> receivedBytes = stackalloc byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    var bytesread = mockStream.Read(receivedBytes);

                    if (bytesread == 0)
                    {
                        stoppingToken.WaitHandle.WaitOne(100);
                    }

                    var readMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    mockStream.Write(receivedBytes[..bytesread]);

                    var writeMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    //_logger.LogInformation("Raw bytes: S {Write:0.00}ms, R {Read:0.00}ms, T {Total:0.00}ms", writeMs, readMs, writeMs + readMs);

                    //string receivedStr = BytesHelpers.DecodeArray(receivedBytes[..bytesread]);

                    //_logger.LogInformation("received {DAT} bytes", receivedStr.Length);
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {ClientAddress}: {Error}", iPAddress, ex.Message);
                }
            }

            _logger.LogInformation("Stream pipe {ClientAddress} ended", iPAddress);

        }, stoppingToken);
    }
}
