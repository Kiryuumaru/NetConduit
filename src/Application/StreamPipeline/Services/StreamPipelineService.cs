using Application.Common;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Pipes;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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

    public void Create(StreamMultiplexer streamMultiplexer, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Create));

        if (_streamMultiplexer != null)
        {
            throw new Exception($"{nameof(StreamPipelineService)} already created");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing());

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

    public MessagingPipe<TSend, TReceive> SetMessagingPipe<TSend, TReceive>(Guid channelKey, string channelName, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var messagingPipe = _serviceProvider.GetRequiredService<MessagingPipe<TSend, TReceive>>();
        var tranceiverStream = GetMux().Set(channelKey, StreamPipelineDefaults.EdgeCommsBufferSize);
        var pipeToken = CancellationTokenSource.CreateLinkedTokenSource(
            _cts!.Token,
            CancelWhenDisposing(),
            messagingPipe.CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing());
        messagingPipe.SetPipeName(channelName);
        messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
        messagingPipe.Start(tranceiverStream, pipeToken.Token).Forget();
        return messagingPipe;
    }

    public CommandPipe<TCommand, TResponse> SetCommandPipe<TCommand, TResponse>(Guid channelKey, string channelName, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var messagingPipe = _serviceProvider.GetRequiredService<CommandPipe<TCommand, TResponse>>();
        var tranceiverStream = GetMux().Set(channelKey, StreamPipelineDefaults.EdgeCommsBufferSize);
        var pipeToken = CancellationTokenSource.CreateLinkedTokenSource(
            _cts!.Token,
            CancelWhenDisposing(),
            messagingPipe.CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing());
        messagingPipe.SetPipeName(channelName);
        messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
        messagingPipe.Start(tranceiverStream, pipeToken.Token).Forget();
        return messagingPipe;
    }

    public MessagingPipe<T, T> SetMessagingPipe<T>(Guid channelKey, string channelName, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return SetMessagingPipe<T, T>(channelKey, channelName, jsonSerializerOptions);
    }
}
