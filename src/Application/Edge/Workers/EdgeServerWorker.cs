using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Models;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
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

        edgeWorkerStartedService.SetIsWorkerStarted(true);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpServer = scope.ServiceProvider.GetRequiredService<TcpServerService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        //await tcpServer.Start(tcpHost, tcpPort, (tcpClient, streamTranceiver, ct) =>
        //{
        //    CancellationToken clientCt = streamTranceiver.CancelWhenDisposing(stoppingToken, ct);

        //    IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;

        //    return Start(clientEndPoint, streamTranceiver, clientCt);

        //}, stoppingToken);
    }
}
