using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services;
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

    private readonly int _bufferSize = 4096;

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
        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpClient.Start(Dns.GetHostEntry(tcpHost).AddressList.Last(), tcpPort, _bufferSize, tranceiverStream =>
        {
            CancellationToken ct = tranceiverStream.CancelWhenDisposing(stoppingToken);

            var streamPipeline = streamPipelineFactory.Pipe(tranceiverStream, _bufferSize, ct);

            Start(streamPipeline, ct);

        }, stoppingToken);
    }

    private async void Start(StreamMultiplexerService streamPipelineService, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stream pipe started");

        while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
        {
            string sendStr = Guid.NewGuid().ToString();
            byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

            try
            {
                DateTimeOffset sendTime = DateTimeOffset.UtcNow;

                var ss = streamPipelineService.Get(StreamPipelineService.CommandChannelKey);

                await ss.WriteAsync(sendBytes, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("{Error}", ex.Message);
            }

            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("Stream pipe ended");
    }
}
