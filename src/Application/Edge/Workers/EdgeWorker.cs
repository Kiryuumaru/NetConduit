using Application.Common;
using Application.Edge.Common;
using Application.Edge.Services;
using Application.Server.Edge.Common;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Server.Edge.Workers;

internal class EdgeWorker(ILogger<EdgeWorker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<EdgeWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var edgeService = scope.ServiceProvider.GetRequiredService<EdgeService>();

        if (!(await edgeService.Get(EdgeDefaults.ServerEdgeEntity.Id, stoppingToken)).SuccessAndHasValue(out var edgeConnectionEntity))
        {
            EdgeTokenEntity newServerEdge = EdgeEntityHelpers.GenerateToken(EdgeDefaults.ServerEdgeEntity);
            edgeConnectionEntity = (await edgeService.Create(newServerEdge, stoppingToken)).GetValueOrThrow();
        }

        _logger.LogInformation("Server Edge was initialized with handshake-token {HandshakeToken}", edgeConnectionEntity.HandshakeToken);

        Start(stoppingToken);
    }

    private void Start(CancellationToken stoppingToken)
    {

    }
}
