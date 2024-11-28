using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Models;
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

        edgeWorkerStartedService.SetValue(true);

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

        //await tcpClient.Start(tcpHost, tcpPort, (tranceiverStream, ct) =>
        //{
        //    CancellationToken clientCt = tranceiverStream.CancelWhenDisposing(stoppingToken, ct);

        //    return Start(tranceiverStream, tcpHost, tcpPort, clientCt);

        //}, stoppingToken);
    }
}
