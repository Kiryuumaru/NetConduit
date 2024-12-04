using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using TestTCPMocker.Configuration.Extensions;
using Microsoft.Extensions.DependencyInjection;
using TestTCPMocker.Services;

namespace TestTCPMocker.Workers;

internal class TCPServerWorker(IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Start(stoppingToken);
        return Task.CompletedTask;
    }

    private async void Start(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        using var server = scope.ServiceProvider.GetRequiredService<TCPServerMocker>();

        int port = int.Parse(_configuration.GetServerConnect()!);

        await server.StartWait(port, stoppingToken);
    }
}
