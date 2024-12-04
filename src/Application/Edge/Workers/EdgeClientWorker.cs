using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application.Edge.Services.Handshake;
using Application.Common.Features;
using Application.Common.Extensions;

namespace Application.Edge.Workers;

internal class EdgeClientWorker(ILogger<EdgeClientWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeClientWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(ExecuteAsync));

        using var scope = _serviceProvider.CreateScope();
        var edgeLocalService = scope.ServiceProvider.GetRequiredService<IEdgeLocalStoreService>();
        var edgeWorkerStartedService = scope.ServiceProvider.GetRequiredService<EdgeWorkerStartedService>();

        var edgeLocalEntity = (await edgeLocalService.GetOrCreate(() => new()
        {
            EdgeType = EdgeType.Client,
            Name = Environment.MachineName
        }, stoppingToken)).GetValueOrThrow();

        if (!(await edgeLocalService.Contains(stoppingToken)).GetValueOrThrow())
        {
            _logger.LogError("Edge initialize local database error");
        }

        _logger.LogInformation("Client edge was initialized with ID {ClientID}", edgeLocalEntity.Id);

        edgeWorkerStartedService.SetOpen(true);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(Routine), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        using var scope = _serviceProvider.CreateScope();
        var tcpClient = scope.ServiceProvider.GetRequiredService<TcpClientService>();

        await Task.Delay(10000, stoppingToken);

        await tcpClient.Start(tcpHost, tcpPort, (tranceiverStream, ct) =>
        {
            var clientCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                ct.Token,
                tranceiverStream.CancelWhenDisposing());

            return Start(tranceiverStream, tcpHost, tcpPort, clientCts);

        }, stoppingToken);
    }

    private async Task Start(TranceiverStream tranceiverStream, string tcpHost, int tcpPort, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(Start), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        var scope = _serviceProvider.CreateScope();
        cts.Token.Register(scope.Dispose);

        var handshakeService = scope.ServiceProvider.GetRequiredService<EdgeClientHandshakeService>();
        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        handshakeService.CancelWhenDisposing().Register(scope.Dispose);
        streamPipelineFactory.CancelWhenDisposing().Register(scope.Dispose);

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} started", tcpHost, tcpPort); },
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} ended", tcpHost, tcpPort); },
            ex => { _logger.LogError("Stream multiplexer {ServerHost}:{ServerPort} error: {Error}", tcpHost, tcpPort, ex.Message); },
            cts.Token);

        var starterGate = new GateKeeper();
        handshakeService.Begin(starterGate, tcpHost, tcpPort, streamPipelineService, cts.Token);
        var workerGate = BeginWorker(handshakeService, tcpHost, tcpPort, streamPipelineService, cts);

        streamPipelineService.Start().Forget();
        starterGate.SetOpen();

        await workerGate.WaitForOpen(cts.Token);
    }

    private GateKeeper BeginWorker(EdgeClientHandshakeService handshakeService, string tcpHost, int tcpPort, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
    {
        var gate = new GateKeeper();

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(BeginWorker), new()
            {
                ["ServerHost"] = tcpHost,
                ["ServerPort"] = tcpPort
            });

            try
            {
                await handshakeService.AcceptGate.WaitForOpen(cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Worker {ServerHost}:{ServerPort} started", tcpHost, tcpPort);

                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                _logger.LogInformation("Worker {ServerHost}:{ServerPort} ended", tcpHost, tcpPort);
            }
            catch (Exception ex)
            {
                _logger.LogError("Worker {ServerHost}:{ServerPort} error: {ErrorMessage}", tcpHost, tcpPort, ex.Message);
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
