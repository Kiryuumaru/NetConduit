using Application.Common.Extensions;
using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineFactory(ILogger<StreamPipelineFactory> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamPipelineFactory> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public StreamPipelineService Create(
        TranceiverStream tranceiverStream,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Create));

        var streamPipelineService = _serviceProvider.GetRequiredService<StreamPipelineService>();
        var streamMultiplexer = StreamMultiplexer.Create(tranceiverStream, onStarted, onStopped, onError);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            CancelWhenDisposing(),
            tranceiverStream.CancelWhenDisposing(),
            streamPipelineService.CancelWhenDisposing());

        cts.Token.Register(streamMultiplexer.Dispose);
        cts.Token.Register(streamPipelineService.Dispose);

        streamPipelineService.Create(streamMultiplexer);

        return streamPipelineService;
    }
}
