using Application.Common.Extensions;
using Application.StreamPipeline.Abstraction;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Features;
using Domain.StreamPipeline.Exceptions;
using Domain.StreamPipeline.Models;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Services.Pipes;

public class MessagingPipe<TSend, TReceive> : BasePipe
{
    private readonly ILogger<MessagingPipe<TSend, TReceive>> _logger;
    private readonly BufferBlock<MessagingPipePayload<TSend>> _messageQueue = new();

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

    public string? Name { get; private set; }

    public JsonSerializerOptions? JsonSerializerOptions { get; private set; }

    public MessagingPipe(ILogger<MessagingPipe<TSend, TReceive>> logger)
    {
        _logger = logger;

        _paddingBytes = Encoding.ASCII.GetBytes(_paddingValue);
        _paddingSize = _paddingBytes.Length;

        _headerSize = _paddingSize + _packetLengthSize + _chunkLengthSize;
        _totalSize = _headerSize + StreamPipelineDefaults.MessagingPipeChunkSize;

        _paddingPos = 0;
        _packetLengthPos = _paddingPos + _paddingSize;
        _chunkLengthPos = _packetLengthPos + _packetLengthSize;
        _chunkPos = _chunkLengthPos + _chunkLengthSize;
    }

    private async Task StartSend(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<TSend, TReceive>), nameof(StartSend), new()
        {
            ["MessagingPipeName"] = Name
        });

        ReadOnlyMemory<byte> paddingBytes = _paddingBytes.AsMemory();
        Memory<byte> headerBytes = new byte[_headerSize];
        Memory<byte> sendBytes = new byte[_totalSize];

        while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
        {
            try
            {
                var messagingPipePayload = await _messageQueue.ReceiveAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                //_logger.LogTrace("MessagingPipe {MessagingPipeName} sending message to stream...", Name);

                var messageBytesArray = JsonSerializer.SerializeToUtf8Bytes(messagingPipePayload, JsonSerializerOptions);
                var messageBytes = messageBytesArray.AsMemory();
                var messageLength = messageBytesArray.LongLength;

                while (messageBytes.Length > 0)
                {
                    var bytesChunkSend = Math.Min(messageBytes.Length, StreamPipelineDefaults.MessagingPipeChunkSize);
                    paddingBytes.CopyTo(sendBytes[.._paddingSize]);
                    BinaryPrimitives.WriteInt64LittleEndian(sendBytes.Slice(_packetLengthPos, _packetLengthSize).Span, messageLength);
                    BinaryPrimitives.WriteInt32LittleEndian(sendBytes.Slice(_chunkLengthPos, _chunkLengthSize).Span, bytesChunkSend);
                    messageBytes[..bytesChunkSend].CopyTo(sendBytes.Slice(_chunkPos, bytesChunkSend));
                    messageBytes = messageBytes[bytesChunkSend..];
                    tranceiverStream.Write(sendBytes[..(_headerSize + bytesChunkSend)].Span);
                }

                //_logger.LogTrace("MessagingPipe {MessagingPipeName} message sent to stream", Name);
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested ||
                    ex is ObjectDisposedException ||
                    ex is OperationCanceledException)
                {
                    break;
                }
                _logger.LogError("MessagingPipe {MessagingPipeName} sender Error: {Error}", Name, ex.Message);
            }
        }
    }

    private async Task StartReceive(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(MessagingPipe<TSend, TReceive>), nameof(StartReceive), new()
        {
            ["MessagingPipeName"] = Name
        });

        ReadOnlyMemory<byte> paddingBytes = _paddingBytes.AsMemory();
        Memory<byte> headerBytes = new byte[_headerSize];
        Memory<byte> receivedBytes = new byte[StreamPipelineDefaults.MessagingPipeChunkSize];

        MemoryStream streamChunkHolder = new(StreamPipelineDefaults.MessagingPipeChunkSize);
        StreamChunkWriter streamChunkWriter = new(streamChunkHolder);

        while (!stoppingToken.IsCancellationRequested && !IsDisposedOrDisposing)
        {
            try
            {
                headerBytes[.._paddingSize].Span.Clear();
                await tranceiverStream.ReadExactlyAsync(headerBytes, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                if (!headerBytes[.._paddingSize].Span.SequenceEqual(paddingBytes.Span))
                {
                    throw CorruptedHeaderBytesException.Instance;
                }

                long packetLength = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(_packetLengthPos, _packetLengthSize).Span);
                int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(_chunkLengthPos, _chunkLengthSize).Span);

                var chunkBytes = receivedBytes[..chunkLength];

                await tranceiverStream.ReadExactlyAsync(chunkBytes, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (streamChunkWriter.WriteChunk(packetLength, chunkBytes.Span))
                {
                    streamChunkHolder.Seek(0, SeekOrigin.Begin);
                    streamChunkHolder.SetLength(packetLength);
                    var packetBytes = streamChunkHolder.ToArray();
                    if (JsonSerializer.Deserialize<MessagingPipePayload<TReceive>>(packetBytes, JsonSerializerOptions) is not MessagingPipePayload<TReceive> messagingPipePayload)
                    {
                        throw new InvalidMessagingPipePayloadException(nameof(MessagingPipePayload<TReceive>));
                    }

                    //_logger.LogTrace("MessagingPipe {MessagingPipeName} received message from stream", Name);

                    _onMessageCallback?.Invoke(messagingPipePayload)?.Forget();
                }
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested ||
                    ex is ObjectDisposedException ||
                    ex is OperationCanceledException)
                {
                    break;
                }
                _logger.LogError("MessagingPipe {MessagingPipeName} receiver Error: {Error}", Name, ex.Message);
            }
        }
    }

    protected override Task Execute(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var ct = CancelWhenDisposing(stoppingToken);

        _logger.LogInformation("MessagingPipe {MessagingPipeName} started", Name);

        return Task.Run(async () =>
        {
            await Task.WhenAll(
                StartSend(tranceiverStream, ct),
                StartReceive(tranceiverStream, ct));

            _logger.LogInformation("MessagingPipe {MessagingPipeName} ended", Name);

        }, ct);
    }

    public void SetPipeName(string name)
    {
        Name = name;
    }

    public void SetJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        JsonSerializerOptions = jsonSerializerOptions;
    }

    public async Task<Guid> Send(TSend message)
    {
        Guid messageGuid = Guid.NewGuid();
        await Send(messageGuid, message);
        return messageGuid;
    }

    public Task Send(Guid messageGuid, TSend message, CancellationToken stoppingToken = default)
    {
        return _messageQueue.SendAsync(new MessagingPipePayload<TSend>()
        {
            MessageGuid = messageGuid,
            Message = message
        }, stoppingToken);
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
