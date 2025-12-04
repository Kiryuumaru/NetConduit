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

        //RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
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

            IPAddress iPAddress = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;

            return Start(iPAddress, tranceiverStream, clientCts);

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
            () => _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress),
            () => _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress),
            ex => _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message));

        Stopwatch loadingSw = Stopwatch.StartNew();
        GateKeeperCounter waiterCount = new();

        int channelCount = 0;
        int channelIndex = EdgeDefaults.MockChannelKeyOffset;
        Guid NextChannel()
        {
            channelCount++;
            return new Guid($"00000000-0000-0000-0000-{channelIndex++:D12}");
        }

        List<Func<Task>> moqs = [];
        moqs.Add(async () =>
        {
            await waiterCount.WaitForOpen(cts.Token);
            streamPipelineService.Start().Forget();
            _logger.LogInformation("Loading time {LoadingTimeMs}ms for {ChannelCount} channels", loadingSw.ElapsedMilliSeconds(), channelCount);
        });
        for (int i = 0; i < EdgeDefaults.RawMockChannelCount; i++)
        {
            var moqChannel = NextChannel();
            TranceiverStream mockStream = new(
                new BlockingMemoryStream(EdgeDefaults.EdgeCommsBufferSize),
                new BlockingMemoryStream(EdgeDefaults.EdgeCommsBufferSize));
            streamPipelineService.SetRaw(moqChannel, mockStream);
            moqs.Add(() => StartMockStreamRaw(mockStream, waiterCount, iPAddress, cts.Token));
        }
        for (int i = 0; i < EdgeDefaults.MsgMockChannelCount; i++)
        {
            var moqChannel = NextChannel();
            var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>($"MOOCK-{moqChannel}");
            moqs.Add(() => StartMockStreamMessaging(mockStream, waiterCount, iPAddress, cts.Token));
        }

        return Task.WhenAll(moqs.Select(i => i()));
    }

    private Task StartMockStreamMessaging(MessagingPipe<MockPayload, MockPayload> mockStream, GateKeeperCounter waiterCount, IPAddress iPAddress, CancellationToken stoppingToken)
    {
        stoppingToken.Register(mockStream.Dispose);

        mockStream.OnMessage(async payload =>
        {
            await mockStream.Send(new()
            {
                MockMessage = JsonSerializer.Serialize(payload)
            });
        });

        waiterCount.Increment();

        return Task.Run(async () =>
        {
            await Task.Delay(1000, stoppingToken);

            waiterCount.Decrement();
            await waiterCount.WaitForOpen(stoppingToken);

            _logger.LogInformation("Mock messaging pipe {iPAddress} started", iPAddress);

        }, stoppingToken);
    }

    private Task StartMockStreamRaw(TranceiverStream mockStream, GateKeeperCounter waiterCount, IPAddress iPAddress, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stream pipe {iPAddress} started", iPAddress);

        waiterCount.Increment();

        return Task.Run(async () =>
        {
            await Task.Delay(1000, stoppingToken);

            waiterCount.Decrement();
            await waiterCount.WaitForOpen(stoppingToken);

            _logger.LogInformation("Mock raw bytes {iPAddress} started", iPAddress);

            Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    var bytesread = await mockStream.ReadAsync(receivedBytes, stoppingToken);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var readMs = sw.ElapsedMilliSeconds();
                    sw.Restart();

                    mockStream.Write(receivedBytes[..bytesread].Span);

                    var writeMs = sw.ElapsedMilliSeconds();
                    sw.Restart();
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
