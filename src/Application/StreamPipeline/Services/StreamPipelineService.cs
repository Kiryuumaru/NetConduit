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

    public StreamMultiplexer Pipe(TranceiverStream tranceiverStream, int bufferSize, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(StreamPipelineService), nameof(Pipe));

        var streamPipelineService = StreamMultiplexer.Create(
            tranceiverStream,
            bufferSize,
            () => _logger.LogTrace("Pipe started"),
            () => _logger.LogTrace("Pipe ended"),
            err => _logger.LogError("{ErrorMessage}", err.Message),
            stoppingToken);
        streamPipelineService.Set(CommandChannelKey, new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize)));
        streamPipelineService.Set(LogChannelKey, new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize)));
        streamPipelineService.Start();

        return streamPipelineService;
    }
}
