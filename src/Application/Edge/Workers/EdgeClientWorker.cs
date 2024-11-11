using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
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

internal class EdgeClientWorker(ILogger<EdgeClientWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeClientWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
        return Task.CompletedTask;
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpClient = scope.ServiceProvider.GetRequiredService<TcpClientService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpClient.Start(Dns.GetHostEntry(tcpHost).AddressList.Last(), tcpPort, 4096, tranceiverStream =>
        {
            Start(tranceiverStream, stoppingToken);

        }, stoppingToken);
    }

    private async void Start(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stream pipe started");

        while (!stoppingToken.IsCancellationRequested && !tranceiverStream.IsDisposedOrDisposing)
        {
            string sendStr = Guid.NewGuid().ToString();
            byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

            try
            {
                DateTimeOffset sendTime = DateTimeOffset.UtcNow;

                await tranceiverStream.WriteAsync(sendBytes, stoppingToken);
                byte[] receivedBytes = new byte[4096];
                await tranceiverStream.ReadAsync(receivedBytes, stoppingToken);

                DateTimeOffset receivedTime = DateTimeOffset.UtcNow;

                string receivedStr = Encoding.Default.GetString(receivedBytes.Where(x => x != 0).ToArray());

                if (sendStr != receivedStr)
                {
                    _logger.LogError("Mismatch: {Sent} != {Received}", sendStr, receivedStr);
                }
                else
                {
                    _logger.LogInformation("Received time {TimeStamp}ms...", (receivedTime - sendTime).TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("{Error}", ex.Message);
            }

            await Task.Delay(100, stoppingToken);
        }

        _logger.LogInformation("Stream pipe ended");
    }
}
