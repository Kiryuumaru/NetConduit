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
using Microsoft.Extensions.DependencyInjection;
using TestTCPMocker.Services;

namespace TestTCPMocker.Workers;

internal class TCPClientWorker(IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
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
        using var client = scope.ServiceProvider.GetRequiredService<TCPClientMocker>();

        var clientToMoq = _configuration.GetClientConnect()!.Split(':');

        await client.StartWait(clientToMoq[0], int.Parse(clientToMoq[1]), stoppingToken);
    }
}
