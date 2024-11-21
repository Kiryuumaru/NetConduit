using Application.Common;
using Application.Edge.Common;
using Application.StreamPipeline.Abstraction;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Pipes;

public class MessagingPipe<T>(ILogger<MessagingPipe<T>> logger) : BasePipe
{
    private readonly ILogger<MessagingPipe<T>> _logger = logger;
    private readonly BufferBlock<MessagingPipePayload<T>> _messageQueue = new();

    private string? _messagingPipeName = null;
    private JsonSerializerOptions? _jsonSerializerOptions = null;
    private Func<MessagingPipePayload<T>, Task>? _onMessageCallback = null;

    private const string _paddingValue = "endofchunk";
    private const int _paddingSize = 10;

    private async Task StartSend(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<T>), nameof(StartSend), new()
        {
            ["MessagingPipeName"] = _messagingPipeName
        });

        _logger.LogTrace("MessagingPipe {MessagingPipeName} sender started", _messagingPipeName);

        while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
        {
            try
            {
                var messagingPipePayload = await _messageQueue.ReceiveAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                //_logger.LogTrace("MessagingPipe {MessagingPipeName} sending message to stream...", _messagingPipeName);

                var serializedMessage = JsonSerializer.Serialize(messagingPipePayload, _jsonSerializerOptions);
                byte[] sendBytes = Encoding.Default.GetBytes(serializedMessage);

                await tranceiverStream.WriteAsync(sendBytes, stoppingToken);

                //_logger.LogTrace("MessagingPipe {MessagingPipeName} message sent to stream", _messagingPipeName);
            }
            catch (Exception ex)
            {
                _logger.LogError("MessagingPipe {MessagingPipeName} sender Error: {Error}", _messagingPipeName, ex.Message);
            }
        }

        _logger.LogTrace("MessagingPipe {MessagingPipeName} sender ended", _messagingPipeName);
    }

    private async Task StartReceive(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<T>), nameof(StartReceive), new()
        {
            ["MessagingPipeName"] = _messagingPipeName
        });

        _logger.LogTrace("MessagingPipe {MessagingPipeName} receiver started", _messagingPipeName);

        Memory<byte> receivedBytes = new byte[EdgeDefaults.EdgeCommsBufferSize];

        while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
        {
            try
            {
                var bytesRead = await tranceiverStream.ReadAsync(receivedBytes, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                //_logger.LogTrace("MessagingPipe {MessagingPipeName} received message from stream", _messagingPipeName);

                string receivedStr = Encoding.Default.GetString(receivedBytes[..bytesRead].Span);

                try
                {
                    if (JsonSerializer.Deserialize<MessagingPipePayload<T>>(receivedStr) is not MessagingPipePayload<T> messagingPipePayload)
                    {
                        throw new Exception($"Message is not {nameof(MessagingPipePayload<T>)}");
                    }

                    _onMessageCallback?.Invoke(messagingPipePayload);
                }
                catch
                {
                    _logger.LogError("SSSSSSSSSSSSSSSSSSSS: {Error}", receivedStr);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("MessagingPipe {MessagingPipeName} receiver Error: {Error}", _messagingPipeName, ex.Message);
            }
        }

        _logger.LogTrace("MessagingPipe {MessagingPipeName} receiver ended", _messagingPipeName);
    }

    protected override Task Execute(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var ct = CancelWhenDisposing(stoppingToken);

        _logger.LogInformation("MessagingPipe {MessagingPipeName} started", _messagingPipeName);

        return Task.Run(async () =>
        {
            await Task.WhenAll(
                StartSend(tranceiverStream, ct),
                StartReceive(tranceiverStream, ct));

            _logger.LogInformation("MessagingPipe {MessagingPipeName} ended", _messagingPipeName);

        }, stoppingToken);
    }

    public void SetPipeName(string name)
    {
        _messagingPipeName = name;
    }

    public void SetJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    public Guid Send(T message)
    {
        Guid msgGuid = Guid.NewGuid();
        _messageQueue.Post(new MessagingPipePayload<T>()
        {
            MessageGuid = msgGuid,
            Message = message
        });
        return msgGuid;
    }

    public void OnMessage(Func<MessagingPipePayload<T>, Task> onMessageCallback)
    {
        _onMessageCallback = onMessageCallback;
    }

    public void OnMessage(Action<MessagingPipePayload<T>> onMessageCallback)
    {
        _onMessageCallback = msgPayload =>
        {
            onMessageCallback(msgPayload);
            return Task.CompletedTask;
        };
    }
}
