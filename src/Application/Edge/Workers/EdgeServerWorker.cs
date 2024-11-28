using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Mockers;
using Application.Edge.Models;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Enums;
using Microsoft.AspNetCore.Routing;
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
            _logger.LogError("Edge initialize local database error");
        }

        _logger.LogInformation("Server edge was initialized with ID {ServerID}", edgeHiveEntity.Id);
        _logger.LogInformation("Server edge was initialized with handshake-token {HandshakeToken}", edgeHiveEntity.Token);

        edgeWorkerStartedService.SetValue(true);

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

        var streamPipelineService = streamPipelineFactory.Start(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress); },
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress); },
            ex => { _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message); },
            cts.Token);

        await StartClientHandshake(iPAddress, streamPipelineService, cts);

        if (cts.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("Steam will not starttttt {ClientAddress} accepted", iPAddress);

        await Task.Delay(999999999, cts.Token);

        //return Task.WhenAll(
        //    StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey9, streamPipelineService, stoppingToken));
    }

    private Task StartClientHandshake(IPAddress iPAddress, StreamPipelineService streamPipelineService, CancellationTokenSource cts)
    {
        var handshakeMessaging = streamPipelineService.SetMessagingPipe<HandshakePayload>(EdgeDefaults.HandshakeChannel, $"handshake_channel");

        return Task.Run(() =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(StartClientHandshake), new()
            {
                ["ClientAddress"] = iPAddress
            });

            _logger.LogInformation("Attempting handshake to {ClientAddress}...", iPAddress);

            ManualResetEventSlim acceptedEvent = new(false);

            handshakeMessaging.OnMessage(payload =>
            {
                if (payload.Message.MockMessage == "conn")
                {
                    handshakeMessaging.Send(new HandshakePayload() { MockMessage = "ok" });
                    acceptedEvent.Set();
                }
            });

            var timeoutCt = cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout);

            try
            {
                acceptedEvent.Wait(timeoutCt);
            }
            catch { }

            if (timeoutCt.IsCancellationRequested)
            {
                _logger.LogInformation("Handshake {ClientAddress} expired", iPAddress);
                cts.Cancel();
            }
            else
            {
                _logger.LogInformation("Handshake {ClientAddress} accepted", iPAddress);
            }
        });
    }
}
