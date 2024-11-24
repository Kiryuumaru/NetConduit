using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Models;
using Application.Edge.Services;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
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
        var tcpHost = _configuration.GetServerTcpHost();
        var tcpPort = _configuration.GetServerTcpPort();

        using var _ = _logger.BeginScopeMap(nameof(EdgeClientWorker), nameof(Routine), new()
        {
            ["ServerHost"] = tcpHost,
            ["ServerPort"] = tcpPort
        });

        using var scope = _serviceProvider.CreateScope();
        var tcpClient = scope.ServiceProvider.GetRequiredService<TcpClientService>();

        await Task.Delay(10000, stoppingToken);

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

        var scope = _serviceProvider.CreateScope();

        stoppingToken.Register(scope.Dispose);

        var streamPipelineFactory = scope.ServiceProvider.GetRequiredService<StreamPipelineFactory>();

        var streamPipelineService = streamPipelineFactory.Start(
            tranceiverStream,
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} started", tcpHost, tcpPort); },
            () => { _logger.LogInformation("Stream multiplexer {ServerHost}:{ServerPort} ended", tcpHost, tcpPort); },
            ex => { _logger.LogError("Stream multiplexer {ServerHost}:{ServerPort} error: {Error}", tcpHost, tcpPort, ex.Message); },
            stoppingToken);

        //return StartMockStreamMessaging(streamPipelineService, stoppingToken);
        //return StartMockStreamRaw(streamPipelineService, tcpHost, tcpPort, stoppingToken);
        //return Task.WhenAll(
        //    StartMockStreamRaw(EdgeDefaults.MockChannelKey0, streamPipelineService, tcpHost, tcpPort, stoppingToken));
        return Task.WhenAll(
            StartMockStreamRaw(EdgeDefaults.MockChannelKey0, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey1, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey2, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey3, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey4, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey5, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey6, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey7, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey8, streamPipelineService, tcpHost, tcpPort, stoppingToken),
            StartMockStreamRaw(EdgeDefaults.MockChannelKey9, streamPipelineService, tcpHost, tcpPort, stoppingToken),
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

        //return Task.WhenAll(
        //    StartMockStreamMessaging(streamPipelineService, stoppingToken),
        //    StartMockStreamRaw(streamPipelineService, tcpHost, tcpPort, stoppingToken));
    }

    int msgStreamAveLent = 100;
    TimeSpan msgStreamLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset msgStreamLastLog = DateTimeOffset.MinValue;
    List<double> msgStreamAveLi = [];
    private Task StartMockStreamMessaging(Guid channelKey, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetMessagingPipe<MockPayload>(channelKey, $"MOOCK-{channelKey}");

        ConcurrentDictionary<Guid, (MockPayload payload, DateTimeOffset time)> mapMock = [];

        mockStream.OnMessage(async payload =>
        {
            var now = DateTimeOffset.UtcNow;

            await Task.Delay(100);

            if (JsonSerializer.Deserialize<MessagingPipePayload<MockPayload>>(payload.Message.MockMessage) is not MessagingPipePayload<MockPayload> clientPayload)
            {
                _logger.LogError("Invalid payload received {PayloadMessageGuid}", payload.MessageGuid);
                return;
            }
            if (!mapMock.TryGetValue(clientPayload.MessageGuid, out var mock))
            {
                _logger.LogError("Unknown payload received {PayloadMessageGuid}", clientPayload.MessageGuid);
                return;
            }
            if (mock.payload.MockMessage != clientPayload.Message.MockMessage)
            {
                _logger.LogError("Mismatch payload received value {PayloadMessageGuid}", payload.MessageGuid);
            }

            msgStreamAveLi.Add((now - mock.time).TotalMilliseconds);
            while (msgStreamAveLi.Count > msgStreamAveLent)
            {
                msgStreamAveLi.RemoveAt(0);
            }
            if (msgStreamLastLog + msgStreamLogSpan < now)
            {
                msgStreamLastLog = now;
                _logger.LogInformation("Messaging mock time {TimeStamp:0.###}ms", msgStreamAveLi.Average());
            }

            mapMock.Remove(clientPayload.MessageGuid, out _);
        });

        return Task.WhenAll(
            Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    string mockVal = StringEncoder.Random(Random.Shared.Next(20000, 50000));
                    var payload = new MockPayload() { MockMessage = mockVal };
                    var now = DateTimeOffset.UtcNow;
                    var guid = mockStream.Send(payload);

                    mapMock[guid] = (payload, now);

                    await Task.Delay(50);
                }

            }, stoppingToken),
            Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Mock messaging map count: {MapCount}", mapMock.Count);

                    await Task.Delay(2000);
                }

            }, stoppingToken));
    }

    int mockStreamRawAveLent = 100;
    TimeSpan mockStreamRawLogSpan = TimeSpan.FromSeconds(1);
    DateTimeOffset mockStreamRawLastLog = DateTimeOffset.MinValue;
    List<double> mockStreamRawAveLi = [];
    SemaphoreSlim aveLocker = new(1);
    private Task StartMockStreamRaw(Guid channelKey, StreamPipelineService streamPipelineService, string tcpHost, int tcpPort, CancellationToken stoppingToken)
    {
        var mockStream = streamPipelineService.SetRaw(channelKey, EdgeDefaults.EdgeCommsBufferSize);

        _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} started", tcpHost, tcpPort);


        return Task.Run(async () =>
        {
            Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

            while (!stoppingToken.IsCancellationRequested && !streamPipelineService.IsDisposedOrDisposing)
            {
                var ict = stoppingToken.WithTimeout(TimeSpan.FromMinutes(5));

                string sendStr = StringEncoder.Random(10001);
                //string sendStr = StringEncoder.Random(Random.Shared.Next(10000));
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    DateTimeOffset sendTime = DateTimeOffset.UtcNow;

                    await mockStream.WriteAsync(sendBytes, ict);
                    var bytesRead = await mockStream.ReadAsync(receivedBytes, ict);

                    DateTimeOffset receivedTime = DateTimeOffset.UtcNow;

                    string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesRead].Span);

                    if (sendStr != receivedStr)
                    {
                        _logger.LogError("Mismatch: {Sent} bytes != {Received} bytes", sendStr.Length, receivedStr.Length);
                    }
                    else
                    {
                        try
                        {
                            await aveLocker.WaitAsync(stoppingToken);

                            mockStreamRawAveLi.Add((receivedTime - sendTime).TotalMilliseconds);
                            while (mockStreamRawAveLi.Count > mockStreamRawAveLent)
                            {
                                mockStreamRawAveLi.RemoveAt(0);
                            }
                            if (mockStreamRawLastLog + mockStreamRawLogSpan < DateTimeOffset.UtcNow)
                            {
                                mockStreamRawLastLog = DateTimeOffset.UtcNow;
                                _logger.LogInformation("Received time {TimeStamp:0.###}ms", mockStreamRawAveLi.Average());
                            }
                        }
                        finally
                        {
                            aveLocker.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error {ServerHost}:{ServerPort}: {Error}", tcpHost, tcpPort, ex.Message);
                }

                await Task.Delay(50, stoppingToken);
            }

            _logger.LogInformation("Stream pipe {ServerHost}:{ServerPort} ended", tcpHost, tcpPort);

        }, stoppingToken);
    }
}
