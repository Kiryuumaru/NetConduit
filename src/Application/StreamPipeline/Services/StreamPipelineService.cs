using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public static readonly Guid CommandChannelKey = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid LogChannelKey = new("00000000-0000-0000-0000-000000000002");

    public StreamMultiplexerService Pipe(TranceiverStream tranceiverStream, int bufferSize, CancellationToken stoppingToken)
    {
        var streamPipelineService = _serviceProvider.GetRequiredService<StreamMultiplexerService>();
        TranceiverStream commandChannel = new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize));
        TranceiverStream logChannel = new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize));
        streamPipelineService.Set(CommandChannelKey, commandChannel);
        //streamPipelineService.Set(LogChannelKey, logChannel);
        streamPipelineService.Start(tranceiverStream, bufferSize, stoppingToken);
        return streamPipelineService;
    }
}
