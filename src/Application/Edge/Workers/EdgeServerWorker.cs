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
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Workers;

internal class EdgeServerWorker(ILogger<EdgeServerWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<EdgeServerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    public static readonly Guid MockChannelKey = new("00000000-0000-0000-0000-000000001234");

    private readonly int _bufferSize = 16384;

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

        using var scope = _serviceProvider.CreateScope();
        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineService>();

        var streamMultiplexer = streamPipelineFactory.Start(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} started", iPAddress); },
            () => { _logger.LogInformation("Stream multiplexer {ClientAddress} ended", iPAddress); },
            ex => { _logger.LogError("Stream multiplexer error {ClientAddress}: {Error}", iPAddress, ex.Message); },
            stoppingToken);

        _logger.LogInformation("Stream pipe {ClientAddress} started", iPAddress);

        return Task.Run(() => {

            var mockStream = streamMultiplexer.Set(MockChannelKey, _bufferSize);

            Span<byte> receivedBytes = stackalloc byte[_bufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamMultiplexer.IsDisposedOrDisposing)
            {
                try
                {
                    var bytesread = mockStream.Read(receivedBytes);

                    if (bytesread == 0)
                    {
                        stoppingToken.WaitHandle.WaitOne(100);
                    } 

                    mockStream.Write(receivedBytes[..bytesread]);

                    string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesread]);

                    _logger.LogInformation("received {DAT} bytes", receivedStr.Length);
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
