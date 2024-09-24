using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TestTCPMocker.Configuration.Extensions;

namespace TestTCPMocker.Workers;

internal class TCPSenderWorker(ILogger<TCPSenderWorker> logger, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<TCPSenderWorker> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    bool isServerMode = false;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        isServerMode = _configuration.GetIsServerMode();
        RoutineExecutor.Execute(TimeSpan.FromSeconds(10), true, stoppingToken, Start, ex => _logger.LogError("TCPSenderWorker Routine error: {Error}", ex.Message));
        return Task.CompletedTask;
    }

    private async Task Start(CancellationToken stoppingToken)
    {

    }
}
