using Application.Common.Extensions;
using Application.Common.Features;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Edge.Services.Handshake;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Application.Edge.Workers;

internal class EdgeServerWorker(ILogger<EdgeServerWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeServerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(ExecuteAsync));

        using var scope = _serviceProvider.CreateScope();
        var edgeLocalService = scope.ServiceProvider.GetRequiredService<IEdgeLocalStoreService>();
        var edgeHiveService = scope.ServiceProvider.GetRequiredService<IEdgeHiveStoreService>();
        var edgeWorkerStartedService = scope.ServiceProvider.GetRequiredService<EdgeWorkerStartedService>();

        var edgeLocalEntity = (await edgeLocalService.GetOrCreate(() => new()
        {
            EdgeType = EdgeType.Server,
            Name = Environment.MachineName
        }, stoppingToken)).GetValueOrThrow();

        var edgeHiveEntity = (await edgeHiveService.GetOrCreate(edgeLocalEntity.Id.ToString(), () => new()
        {
            EdgeType = EdgeType.Server,
            Name = Environment.MachineName
        }, stoppingToken)).GetValueOrThrow();

        if (!(await edgeLocalService.Contains(stoppingToken)).GetValueOrThrow() ||
            !(await edgeHiveService.Contains(edgeHiveEntity.Id.ToString(), stoppingToken)).GetValueOrThrow())
        {
            throw new Exception("Edge initialize local database error");
        }

        _logger.LogInformation("Server edge was initialized with ID {ServerID}", edgeLocalEntity.Id);
        _logger.LogInformation("Server edge was initialized with handshake-token {HandshakeToken}", edgeLocalEntity.Token);

        edgeWorkerStartedService.SetOpen(true);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpServer = scope.ServiceProvider.GetRequiredService<TcpServerService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpServer.Start(tcpHost, tcpPort, (tcpClient, streamTranceiver, ct) =>
        {
            var clientCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                ct.Token,
                streamTranceiver.CancelWhenDisposing());

            IPAddress clientEndPoint = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address!;

            return Start(clientEndPoint, streamTranceiver, clientCts);

        }, stoppingToken);
    }

    private async Task Start(IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Start), new()
        {
            ["ClientAddress"] = iPAddress
        });

        var scope = _serviceProvider.CreateScope();
        cts.Token.Register(scope.Dispose);

        var handshakeService = scope.ServiceProvider.GetRequiredService<EdgeServerHandshakeService>();
        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        handshakeService.CancelWhenDisposing().Register(scope.Dispose);
        streamPipelineFactory.CancelWhenDisposing().Register(scope.Dispose);

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress),
            () => _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress),
            ex => _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message),
            cts.Token);

        var starterGate = new GateKeeper();
        handshakeService.Begin(starterGate, iPAddress, streamPipelineService, cts.Token);
        var workerGate = BeginWorker(handshakeService, iPAddress, streamPipelineService, cts);

        streamPipelineService.Start().Forget();
        starterGate.SetOpen();

        await workerGate.WaitForOpen(cts.Token);
    }

    private GateKeeper BeginWorker(EdgeServerHandshakeService handshakeService, IPAddress iPAddress, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
    {
        var gate = new GateKeeper();

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(BeginWorker), new()
            {
                ["ClientAddress"] = iPAddress
            });

            try
            {
                await handshakeService.AcceptGate.WaitForOpen(cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Worker {ClientAddress} started", iPAddress);

                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                _logger.LogInformation("Worker {ClientAddress} ended", iPAddress);
            }
            catch (Exception ex)
            {
                _logger.LogError("Worker {ClientAddress} error: {ErrorMessage}", iPAddress, ex.Message);
                cts.Cancel();
            }
            finally
            {
                gate.SetOpen();
            }
        });

        return gate;
    }
}
