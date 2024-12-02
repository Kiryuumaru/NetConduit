using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Mockers;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Enums;
using Domain.Edge.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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

        var edgeLocalEntity1 = (await edgeLocalService.GetOrCreate(() => new()
        {
            EdgeType = EdgeType.Server,
            Name = Environment.MachineName
        }, stoppingToken));

        foreach (var er in edgeLocalEntity1.Errors)
        {
            _logger.LogError("Error1111: {Error}", er.Message);
        }

        var edgeLocalEntity = edgeLocalEntity1.GetValueOrThrow();

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

        _logger.LogInformation("Server edge was initialized with ID {ServerID}", edgeHiveEntity.Id);
        _logger.LogInformation("Server edge was initialized with handshake-token {HandshakeToken}", edgeHiveEntity.Token);

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

            IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;

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

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Create(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress); },
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress); },
            ex => { _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message); },
            cts.Token);

        var starterGate = new GateKeeper();
        var handshakeGate = BeginHandshake(starterGate, iPAddress, streamPipelineService, cts);
        var workerGate = BeginWorker(handshakeGate, iPAddress, streamPipelineService, cts);

        streamPipelineService.Start().Forget();
        starterGate.SetOpen();

        await workerGate.WaitForOpen(cts.Token);
    }

    private GateKeeper BeginHandshake(GateKeeper dependent, IPAddress iPAddress, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
    {
        var gate = new GateKeeper();

        var handshakeCommand = streamPipelineService.SetCommandPipe<HandshakePayload, HandshakePayload>(EdgeDefaults.HandshakeChannel, $"handshake_channel");

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(BeginHandshake), new()
            {
                ["ClientAddress"] = iPAddress
            });

            try
            {
                await dependent.WaitForOpen(cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Attempting handshake from {ClientAddress}...", iPAddress);

                var acceptGate = new GateKeeper();

                handshakeCommand.OnCommand(callback =>
                {
                    if (callback.Command.MockMessage == "conn")
                    {
                        acceptGate.SetOpen();
                        callback.Respond(new HandshakePayload() { MockMessage = "ok" });
                    }
                });

                if (await acceptGate.WaitForOpen(cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout)))
                {
                    _logger.LogInformation("Handshake {ClientAddress} accepted", iPAddress);
                }
                else
                {
                    _logger.LogInformation("Handshake {ClientAddress} declined: Expired", iPAddress);
                    cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Handshake {ClientAddress} declined: {ErrorMessage}", iPAddress, ex.Message);
                cts.Cancel();
            }
            finally
            {
                gate.SetOpen();
            }
        });

        return gate;
    }

    private GateKeeper BeginWorker(GateKeeper dependent, IPAddress iPAddress, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
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
                await dependent.WaitForOpen(cts.Token);
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
