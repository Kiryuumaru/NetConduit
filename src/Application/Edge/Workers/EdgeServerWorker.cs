using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Tcp.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
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
using System.Text;
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
        var edgeService = scope.ServiceProvider.GetRequiredService<IEdgeService>();

        if (!(await edgeService.Contains(EdgeDefaults.ServerEdgeId.ToString(), stoppingToken)).SuccessAndHasValue(out var contains) || !contains ||
            !(await edgeService.GetToken(EdgeDefaults.ServerEdgeId.ToString(), stoppingToken)).SuccessAndHasValue(out var edgeConnectionEntity))
        {
            AddEdgeDto newServerEdge = new()
            {
                Id = EdgeDefaults.ServerEdgeId,
                Name = EdgeDefaults.ServerEdgeName
            };
            edgeConnectionEntity = (await edgeService.Create(newServerEdge, stoppingToken)).GetValueOrThrow();
        }

        _logger.LogInformation("Server edge was initialized with handshake-token {HandshakeToken}", edgeConnectionEntity.Token);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpServer = scope.ServiceProvider.GetRequiredService<TcpServerService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpServer.Start(Dns.GetHostEntry(tcpHost).AddressList.Last(), tcpPort, 4096, (tcpClient, ns) =>
        {
            return new()
            {
                ReceiverStream = ns
            };
        }, stoppingToken);
    }
}
