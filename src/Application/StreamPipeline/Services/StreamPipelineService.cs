using Application.Common;
using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(ILogger<StreamPipelineService> logger)
{
    private readonly ILogger<StreamPipelineService> _logger = logger;

    private StreamMultiplexer? _streamMultiplexer = null;

    private CancellationTokenSource? _cts;

    private StreamMultiplexer GetMux()
    {
        return _streamMultiplexer ?? throw new Exception($"{nameof(StreamPipelineService)} not started");
    }

    public void Start(StreamMultiplexer streamMultiplexer, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Start));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing());

        _streamMultiplexer = streamMultiplexer;

        _streamMultiplexer.Set(StreamPipelineDefaults.CommandChannelKey, StreamPipelineDefaults.EdgeCommsBufferSize);

        _streamMultiplexer.Start().Forget();
    }

    public TranceiverStream Set(Guid channelKey, int bufferSize)
    {
        return GetMux().Set(channelKey, bufferSize);
    }
}
