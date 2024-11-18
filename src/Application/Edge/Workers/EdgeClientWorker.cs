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

    public static readonly Guid MockChannelKey = new("00000000-0000-0000-0000-000000001234");

    private readonly int _bufferSize = 16384;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
        return Task.CompletedTask;
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

        await tcpClient.Start(tcpHost, tcpPort, (tranceiverStream, ct) =>
        {
            CancellationToken clientCt = tranceiverStream.CancelWhenDisposing(stoppingToken, ct);

            return Start(tranceiverStream, tcpHost, tcpPort, clientCt);

        }, stoppingToken);
    }

    private Task Start(TranceiverStream tranceiverStream, string tcpHost, int tcpPort, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(Start), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        using var scope = _serviceProvider.CreateScope();
        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineService>();

        var streamMultiplexer = streamPipelineFactory.Start(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} started", tcpHost, tcpPort); },
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} ended", tcpHost, tcpPort); },
            ex => { _logger.LogError("Stream multiplexer {ServerHost}:{ServerPort} error: {Error}", tcpHost, tcpPort, ex.Message); },
            stoppingToken);

        _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} started", tcpHost, tcpPort);

        return Task.Run(async () =>
        {
            var mockStream = streamMultiplexer.Set(MockChannelKey, _bufferSize);

            Memory<byte> receivedBytes = new byte[_bufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamMultiplexer.IsDisposedOrDisposing)
            {
                string sendStr = StringEncoder.Random(10000);
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    DateTimeOffset sendTime = DateTimeOffset.UtcNow;

                    await mockStream.WriteAsync(sendBytes, stoppingToken);
                    var bytesRead = await mockStream.ReadAsync(receivedBytes, stoppingToken);

                    DateTimeOffset receivedTime = DateTimeOffset.UtcNow;

                    string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesRead].ToArray());

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
                    _logger.LogError("Error {ServerHost}:{ServerPort}: {Error}", tcpHost, tcpPort, ex.Message);
                }

                await Task.Delay(1000, stoppingToken);
            }

            _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} ended", tcpHost, tcpPort);

        }, stoppingToken);
    }
}
