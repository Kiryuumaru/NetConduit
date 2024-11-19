using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Edge.Workers;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Application.Watchdog.Workers;

internal class GCCollector(ILogger<EdgeClientWorker> logger) : BackgroundService
{
    private readonly ILogger<EdgeClientWorker> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromMinutes(1), true, GCCollect, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
        return Task.CompletedTask;
    }
    private Task GCCollect(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(GCCollector), nameof(GCCollect));
        _logger.LogInformation("GC collection routine");
        GC.Collect();
        return Task.CompletedTask;
    }
}
