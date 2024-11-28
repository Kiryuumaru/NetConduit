using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Models;
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
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Application.Edge.Mockers;

internal class EdgeServerMockWorker(ILogger<EdgeServerMockWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeServerMockWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(ExecuteAsync));

        using var scope = _serviceProvider.CreateScope();
        var edgeLocalService = scope.ServiceProvider.GetRequiredService<IEdgeLocalStoreService>();

        while (!(await edgeLocalService.Contains(stoppingToken)).SuccessAndHasValue(out var contains) || !contains)
        {
            await Task.Delay(1000, stoppingToken);
        }

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(Routine));

        using var scope = _serviceProvider.CreateScope();
        var tcpServer = scope.ServiceProvider.GetRequiredService<TcpServerService>();

        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        await tcpServer.Start(tcpHost, tcpPort, (tcpClient, streamTranceiver, ct) =>
        {
            CancellationToken clientCt = streamTranceiver.CancelWhenDisposing(stoppingToken, ct);

            IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;

            return Start(clientEndPoint, streamTranceiver, clientCt);

        }, stoppingToken);
    }

    private Task Start(IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerMockWorker), nameof(Start), new()
        {
            ["ClientAddress"] = iPAddress
        });

        var scope = _serviceProvider.CreateScope();

        stoppingToken.Register(scope.Dispose);

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Start(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress); },
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress); },
            ex => { _logger.LogError("Stream multiplexer {ClientAddress} error: {Error}", iPAddress, ex.Message); },
            stoppingToken);

        return Task.WhenAll(
            StartMockStreamRaw(EdgeDefaults.MockChannelKey0, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey1, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey2, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey3, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey4, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey5, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey6, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey7, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey8, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey9, streamPipelineService, iPAddress, tranceiverStream, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey0, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey1, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey2, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey3, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey4, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey5, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey6, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey7, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey8, streamPipelineService, stoppingToken),
            StartMockStreamMessaging(EdgeDefaults.MockMsgChannelKey9, streamPipelineService, stoppingToken));
    }

    private Task StartMockStreamMessaging(Guid channelKey, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(channelKey, $"MOOCK-{channelKey}");

        stoppingToken.Register(mockStream.Dispose);

        mockStream.OnMessage(payload =>
        {
            mockStream.Send(new()
            {
                MockMessage = JsonSerializer.Serialize(payload)
            });
        });

        return Task.CompletedTask;
    }

    private Task StartMockStreamRaw(Guid channelKey, StreamPipelineService streamPipelineService, IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetRaw(channelKey, EdgeDefaults.EdgeCommsBufferSize);

        _logger.LogInformation("Stream pipe {ClientAddress} started", iPAddress);

        return Task.Run(() =>
        {
            Span<byte> receivedBytes = stackalloc byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
            {
                try
                {
                    var bytesread = mockStream.Read(receivedBytes);

                    if (bytesread == 0)
                    {
                        stoppingToken.WaitHandle.WaitOne(100);
                    }

                    mockStream.Write(receivedBytes[..bytesread]);

                    //string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesread]);

                    //_logger.LogInformation("received {DAT} bytes", receivedStr.Length);
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {ClientAddress}: {Error}", iPAddress, ex.Message);
                }
            }

            _logger.LogInformation("Stream pipe {ClientAddress} ended", iPAddress);

        }, stoppingToken);
    }
}
