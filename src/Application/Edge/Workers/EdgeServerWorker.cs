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

namespace Application.Edge.Workers;

internal class EdgeServerWorker(ILogger<EdgeServerWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeServerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(ExecuteAsync));

        using var scope = _serviceProvider.CreateScope();
        var edgeService = scope.ServiceProvider.GetRequiredService<IEdgeStoreService>();

        if (!(await edgeService.Contains(EdgeDefaults.ServerEdgeId.ToString(), stoppingToken)).SuccessAndHasValue(out var contains) || !contains ||
            !(await edgeService.GetToken(EdgeDefaults.ServerEdgeId.ToString(), stoppingToken)).SuccessAndHasValue(out var edgeConnectionEntity))
        {
            AddEdgeDto newServerEdge = new()
            {
                Id = EdgeDefaults.ServerEdgeId,
                Name = EdgeDefaults.ServerEdgeName
            };
            edgeConnectionEntity = (await edgeService.Create(newServerEdge, stoppingToken)).GetValueOrThrow();
        }

        _logger.LogInformation("Server edge was initialized with handshake-token {HandshakeToken}", edgeConnectionEntity.Token);

        RoutineExecutor.Execute(TimeSpan.FromSeconds(1), true, Routine, ex => _logger.LogError("Error: {Error}", ex.Message), stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Routine));

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
        using var _ = _logger.BeginScopeMap(nameof(EdgeServerWorker), nameof(Start), new()
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

        //return StartMockStreamMessaging(streamPipelineService, stoppingToken);
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
            StartMockStreamRaw(EdgeDefaults.MockChannelKey9, streamPipelineService, iPAddress, tranceiverStream, stoppingToken));

        //return Task.WhenAll(
        //    StartMockStreamMessaging(streamPipelineService, stoppingToken),
        //    StartMockStreamRaw(streamPipelineService, iPAddress, tranceiverStream, stoppingToken));
    }

    private Task StartMockStreamMessaging(StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(EdgeDefaults.MockMsgChannelKey, "MOOCK");

        mockStream.OnMessage(payload =>
        {
            mockStream.Send(new()
            {
                MockMessage = JsonSerializer.Serialize(payload)
            });
        });

        return Task.CompletedTask;
    }

    private Task StartMockStreamRaw(Guid guid, StreamPipelineService streamPipelineService, IPAddress iPAddress, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.Set(guid, EdgeDefaults.EdgeCommsBufferSize);

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
