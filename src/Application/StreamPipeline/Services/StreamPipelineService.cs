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

    public static readonly Guid CommandChannelKey = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid LogChannelKey = new("00000000-0000-0000-0000-000000000002");

    public StreamMultiplexer Pipe(
        TranceiverStream tranceiverStream,
        int bufferSize,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Pipe));

        var ct = tranceiverStream.CancelWhenDisposing(stoppingToken);

        ct.Register(tranceiverStream.Dispose);

        var streamPipelineService = StreamMultiplexer.Create(tranceiverStream, bufferSize, onStarted, onStopped, onError, ct);

        ct.Register(streamPipelineService.Dispose);

        streamPipelineService.Set(CommandChannelKey, new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize)));
        streamPipelineService.Set(LogChannelKey, new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize)));
        streamPipelineService.Start().Forget();

        return streamPipelineService;
    }
}
