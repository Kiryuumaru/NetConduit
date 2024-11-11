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
public partial class StreamPipelineFactory(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public StreamPipelineService Pipe(TranceiverStream tranceiverStream, int bufferSize, CancellationToken stoppingToken)
    {
        var streamPipelineService = _serviceProvider.GetRequiredService<StreamPipelineService>();
        streamPipelineService.Start(tranceiverStream, bufferSize, stoppingToken);
        return streamPipelineService;
    }
}
