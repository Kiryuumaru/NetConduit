using Application.Common.Extensions;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Interfaces;
using Application.StreamPipeline.Services.Pipes;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(ILogger<StreamPipelineService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamPipelineService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private readonly ConcurrentDictionary<string, Guid> _channelNameMap = [];

    private StreamMultiplexer? _streamMultiplexer = null;

    private CancellationTokenSource? _cts;

    private StreamMultiplexer GetMux()
    {
        return _streamMultiplexer ?? throw new Exception($"{nameof(StreamPipelineService)} not started");
    }

    public void Create(StreamMultiplexer streamMultiplexer)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Create));

        if (_streamMultiplexer != null)
        {
            throw new Exception($"{nameof(StreamPipelineService)} already created");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(CancelWhenDisposing());

        _streamMultiplexer = streamMultiplexer;
    }

    public Task Start()
    {
        if (_streamMultiplexer == null)
        {
            throw new Exception($"{nameof(StreamPipelineService)} is not created");
        }
        return _streamMultiplexer.Start();
    }

    public TranceiverStream SetRaw(Guid channelKey, int bufferSize)
    {
        return GetMux().Set(channelKey, bufferSize);
    }

    public void SetRaw(Guid channelKey, TranceiverStream tranceiverStream)
    {
        GetMux().Set(channelKey, tranceiverStream);
    }

    public MessagingPipe<TSend, TReceive> SetMessagingPipe<TSend, TReceive>(string channelName, Guid? channelKey = null, JsonSerializerOptions? jsonSerializerOptions = null, ISecureStreamFactory? secureStreamFactory = null)
    {
        var messagingPipe = _serviceProvider.GetRequiredService<MessagingPipe<TSend, TReceive>>();

        if (channelKey == null)
        {
            channelKey = GuidExtension.GenerateSeeded(channelName);
        }

        TranceiverStream tranceiverStream;
        if (secureStreamFactory == null)
        {
            tranceiverStream = GetMux().Set(channelKey.Value, StreamPipelineDefaults.EdgeCommsBufferSize);
        }
        else
        {
            tranceiverStream = secureStreamFactory.CreateSecureTranceiverStream(StreamPipelineDefaults.EdgeCommsBufferSize);
            GetMux().Set(channelKey.Value, tranceiverStream);
        }

        var pipeToken = CancellationTokenSource.CreateLinkedTokenSource(
            _cts!.Token,
            messagingPipe.CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing());

        messagingPipe.SetPipeName(channelName);
        messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
        messagingPipe.Start(tranceiverStream, pipeToken.Token).Forget();

        return messagingPipe;
    }

    public MessagingPipe<T, T> SetMessagingPipe<T>(string channelName, Guid? channelKey = null, JsonSerializerOptions? jsonSerializerOptions = null, ISecureStreamFactory? secureStreamFactory = null)
    {
        return SetMessagingPipe<T, T>(channelName, channelKey, jsonSerializerOptions, secureStreamFactory);
    }

    public CommandPipe<TCommand, TResponse> SetCommandPipe<TCommand, TResponse>(string channelName, Guid? channelKey = null, JsonSerializerOptions? jsonSerializerOptions = null, ISecureStreamFactory? secureStreamFactory = null)
    {
        var messagingPipe = _serviceProvider.GetRequiredService<CommandPipe<TCommand, TResponse>>();

        if (channelKey == null)
        {
            channelKey = GuidExtension.GenerateSeeded(channelName);
        }
        TranceiverStream tranceiverStream;
        if (secureStreamFactory == null)
        {
            tranceiverStream = GetMux().Set(channelKey.Value, StreamPipelineDefaults.EdgeCommsBufferSize);
        }
        else
        {
            tranceiverStream = secureStreamFactory.CreateSecureTranceiverStream(StreamPipelineDefaults.EdgeCommsBufferSize);
            GetMux().Set(channelKey.Value, tranceiverStream);
        }

        var pipeToken = CancellationTokenSource.CreateLinkedTokenSource(
            _cts!.Token,
            messagingPipe.CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing());

        messagingPipe.SetPipeName(channelName);
        messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
        messagingPipe.Start(tranceiverStream, pipeToken.Token).Forget();

        return messagingPipe;
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
        }
    }
}
