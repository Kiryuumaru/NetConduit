using Application.Common;
using Application.Edge.Common;
using Application.StreamPipeline.Abstraction;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Exceptions;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Pipes;

public class MessagingPipe<TSend, TReceive> : BasePipe
{
    private readonly ILogger<MessagingPipe<TSend, TReceive>> _logger;
    private readonly BufferBlock<MessagingPipePayload<TSend>> _messageQueue = new();

    private string? _messagingPipeName = null;
    private JsonSerializerOptions? _jsonSerializerOptions = null;
    private Func<MessagingPipePayload<TReceive>, Task>? _onMessageCallback = null;

    private const string _paddingValue = "endofchunk";

    private const int _packetLengthSize = 8;
    private const int _chunkLengthSize = 4;

    private readonly byte[] _paddingBytes;

    private readonly int _paddingSize;
    private readonly int _headerSize;
    private readonly int _totalSize;

    private readonly int _paddingPos;
    private readonly int _packetLengthPos;
    private readonly int _chunkLengthPos;
    private readonly int _chunkPos;

    public MessagingPipe(ILogger<MessagingPipe<TSend, TReceive>> logger)
    {
        _logger = logger;

        _paddingBytes = Encoding.Default.GetBytes(_paddingValue);
        _paddingSize = _paddingBytes.Length;

        _headerSize = _paddingSize + _packetLengthSize + _chunkLengthSize;
        _totalSize = _headerSize + StreamPipelineDefaults.MessagingPipeChunkSize;

        _paddingPos = 0;
        _packetLengthPos = _paddingPos + _paddingSize;
        _chunkLengthPos = _packetLengthPos + _packetLengthSize;
        _chunkPos = _chunkLengthPos + _chunkLengthSize;
    }

    private Task StartSend(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<TSend, TReceive>), nameof(StartSend), new()
            {
                ["MessagingPipeName"] = _messagingPipeName
            });

            Span<byte> paddingBytes = _paddingBytes.AsSpan();
            Span<byte> headerBytes = stackalloc byte[_headerSize];
            Span<byte> sendBytes = stackalloc byte[_totalSize];

            while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
            {
                try
                {
                    var messagingPipePayload = _messageQueue.Receive(stoppingToken);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    //_logger.LogTrace("MessagingPipe {MessagingPipeName} sending message to stream...", _messagingPipeName);

                    var messageBytesArray = JsonSerializer.SerializeToUtf8Bytes(messagingPipePayload, _jsonSerializerOptions);
                    var messageBytes = messageBytesArray.AsSpan();
                    var messageLength = messageBytesArray.LongLength;

                    while (messageBytes.Length > 0)
                    {
                        var bytesChunkSend = Math.Min(messageBytes.Length, StreamPipelineDefaults.MessagingPipeChunkSize);
                        paddingBytes.CopyTo(sendBytes[.._paddingSize]);
                        BinaryPrimitives.WriteInt64LittleEndian(sendBytes.Slice(_packetLengthPos, _packetLengthSize), messageLength);
                        BinaryPrimitives.WriteInt32LittleEndian(sendBytes.Slice(_chunkLengthPos, _chunkLengthSize), bytesChunkSend);
                        messageBytes[..bytesChunkSend].CopyTo(sendBytes.Slice(_chunkPos, bytesChunkSend));
                        messageBytes = messageBytes[bytesChunkSend..];
                        tranceiverStream.Write(sendBytes[..(_headerSize + bytesChunkSend)]);
                    }

                    //_logger.LogTrace("MessagingPipe {MessagingPipeName} message sent to stream", _messagingPipeName);
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("MessagingPipe {MessagingPipeName} sender Error: {Error}", _messagingPipeName, ex.Message);
                }
            }

        }, stoppingToken);
    }

    private Task StartReceive(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<TSend, TReceive>), nameof(StartReceive), new()
            {
                ["MessagingPipeName"] = _messagingPipeName
            });

            Span<byte> paddingBytes = _paddingBytes.AsSpan();
            Span<byte> headerBytes = stackalloc byte[_headerSize];
            Span<byte> receivedBytes = stackalloc byte[StreamPipelineDefaults.MessagingPipeChunkSize];

            MemoryStream streamChunkHolder = new(StreamPipelineDefaults.MessagingPipeChunkSize);
            StreamChunkWriter streamChunkWriter = new(streamChunkHolder);

            while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
            {
                try
                {
                    headerBytes[.._paddingSize].Clear();
                    tranceiverStream.ReadExactly(headerBytes);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    if (!headerBytes[.._paddingSize].SequenceEqual(paddingBytes))
                    {
                        throw CorruptedHeaderBytesException.Instance;
                    }

                    long packetLength = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(_packetLengthPos, _packetLengthSize));
                    int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(_chunkLengthPos, _chunkLengthSize));

                    var chunkBytes = receivedBytes[..chunkLength];

                    tranceiverStream.ReadExactly(chunkBytes);

                    if (streamChunkWriter.WriteChunk(packetLength, chunkBytes))
                    {
                        streamChunkHolder.Seek(0, SeekOrigin.Begin);
                        streamChunkHolder.SetLength(packetLength);
                        var packetBytes = streamChunkHolder.ToArray();
                        if (JsonSerializer.Deserialize<MessagingPipePayload<TReceive>>(packetBytes, _jsonSerializerOptions) is not MessagingPipePayload<TReceive> messagingPipePayload)
                        {
                            throw new InvalidMessagingPipePayloadException(nameof(MessagingPipePayload<TReceive>));
                        }

                        //_logger.LogTrace("MessagingPipe {MessagingPipeName} received message from stream", _messagingPipeName);

                        _onMessageCallback?.Invoke(messagingPipePayload)?.Forget();
                    }
                }
                catch (Exception ex)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("MessagingPipe {MessagingPipeName} receiver Error: {Error}", _messagingPipeName, ex.Message);
                }
            }

        }, stoppingToken);
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

        }, ct);
    }

    public void SetPipeName(string name)
    {
        _messagingPipeName = name;
    }

    public void SetJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    public Guid Send(TSend message)
    {
        Guid msgGuid = Guid.NewGuid();
        _messageQueue.Post(new MessagingPipePayload<TSend>()
        {
            MessageGuid = msgGuid,
            Message = message
        });
        return msgGuid;
    }

    public void OnMessage(Func<MessagingPipePayload<TReceive>, Task> onMessageCallback)
    {
        _onMessageCallback = onMessageCallback;
    }

    public void OnMessage(Action<MessagingPipePayload<TReceive>> onMessageCallback)
    {
        _onMessageCallback = msgPayload =>
        {
            onMessageCallback(msgPayload);
            return Task.CompletedTask;
        };
    }
}
