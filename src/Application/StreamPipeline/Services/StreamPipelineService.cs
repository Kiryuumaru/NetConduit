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

    private const int _commanBufferSize = 4096;
    private const int _logBufferSize = 4096;

    public StreamMultiplexer Start(
        TranceiverStream tranceiverStream,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Start));

        var ct = tranceiverStream.CancelWhenDisposing(stoppingToken);

        ct.Register(tranceiverStream.Dispose);

        var streamPipelineService = StreamMultiplexer.Create(tranceiverStream, onStarted, onStopped, onError, ct);

        ct.Register(streamPipelineService.Dispose);

        streamPipelineService.Set(CommandChannelKey, _commanBufferSize);
        streamPipelineService.Set(LogChannelKey, _logBufferSize);

        streamPipelineService.Start().Forget();

        return streamPipelineService;
    }
}
