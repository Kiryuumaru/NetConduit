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
public partial class StreamPipelineFactory(ILogger<StreamPipelineFactory> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamPipelineFactory> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public StreamPipelineService Start(
        TranceiverStream tranceiverStream,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Start));
        var streamPipelineService = _serviceProvider.GetRequiredService<StreamPipelineService>();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing(),
            streamPipelineService.CancelWhenDisposing());

        var streamMultiplexer = StreamMultiplexer.Create(tranceiverStream, onStarted, onStopped, onError, cts.Token);

        streamPipelineService.Start(streamMultiplexer, cts.Token);

        return streamPipelineService;
    }
}
