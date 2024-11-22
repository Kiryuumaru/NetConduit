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

    public void Start(StreamMultiplexer streamMultiplexer, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Start));

        if (_streamMultiplexer != null)
        {
            throw new Exception($"{nameof(StreamPipelineService)} already started");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing());

        _streamMultiplexer = streamMultiplexer;

        _streamMultiplexer.Set(StreamPipelineDefaults.CommandChannelKey, StreamPipelineDefaults.EdgeCommsBufferSize);

        _streamMultiplexer.Start().Forget();
    }

    public TranceiverStream SetRaw(Guid channelKey, int bufferSize)
    {
        return GetMux().Set(channelKey, bufferSize);
    }

    public MessagingPipe<T> SetMessagingPipe<T>(Guid channelKey, string channelName, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var messagingPipe = _serviceProvider.GetRequiredService<MessagingPipe<T>>();
        var tranceiverStream = GetMux().Set(channelKey, StreamPipelineDefaults.EdgeCommsBufferSize);
        messagingPipe.SetPipeName(channelName);
        messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
        messagingPipe.Start(tranceiverStream, _cts!.Token).Forget();
        return messagingPipe;
    }
}
