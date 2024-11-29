using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Enums;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Edge.Mockers;
using Domain.Edge.Models;

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

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} started", tcpHost, tcpPort); },
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} ended", tcpHost, tcpPort); },
            ex => { _logger.LogError("Stream multiplexer {ServerHost}:{ServerPort} error: {Error}", tcpHost, tcpPort, ex.Message); },
            cts.Token);

        var starterGate = new GateKeeper();
        var handshakeGate = BeginHandshake(starterGate, tcpHost, tcpPort, streamPipelineService, cts);
        var workerGate = BeginWorker(handshakeGate, tcpHost, tcpPort, streamPipelineService, cts);

        streamPipelineService.Start().Forget();
        starterGate.SetOpen();

        await workerGate.WaitForOpen(cts.Token);
    }

    private GateKeeper BeginHandshake(GateKeeper dependent, string tcpHost, int tcpPort, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
    {
        var gate = new GateKeeper();

        var handshakeCommand = streamPipelineService.SetCommandPipe<HandshakePayload, HandshakePayload>(EdgeDefaults.HandshakeChannel, $"handshake_channel");

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(BeginHandshake), new()
            {
                ["ServerHost"] = tcpHost,
                ["ServerPort"] = tcpPort
            });

            try
            {
                await dependent.WaitForOpen(cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Attempting handshake to {ServerHost}:{ServerPort}...", tcpHost, tcpPort);

                var handshakePayload = new HandshakePayload() { MockMessage = "conn" };

                if (!(await handshakeCommand.Send(handshakePayload, cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout))).SuccessAndHasValue(out var handshake) ||
                    handshake.MockMessage != "ok")
                {
                    throw new Exception("Invalid handshake token");
                }

                _logger.LogInformation("Handshake {ServerHost}:{ServerPort} accepted", tcpHost, tcpPort);
            }
            catch (Exception ex)
            {
                _logger.LogError("Handshake {ServerHost}:{ServerPort} declined: {ErrorMessage}", tcpHost, tcpPort, ex.Message);
                cts.Cancel();
            }
            finally
            {
                gate.SetOpen();
            }
        });

        return gate;
    }

    private GateKeeper BeginWorker(GateKeeper dependent, string tcpHost, int tcpPort, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
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
                await dependent.WaitForOpen(cts.Token);
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
