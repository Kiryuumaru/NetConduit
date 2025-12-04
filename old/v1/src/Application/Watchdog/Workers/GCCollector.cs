using Application.Edge.Mockers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application.Common.Extensions;

namespace Application.Watchdog.Workers;

internal class GCCollector(ILogger<EdgeClientMockWorker> logger) : BackgroundService
{
    private readonly ILogger<EdgeClientMockWorker> _logger = logger;

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
