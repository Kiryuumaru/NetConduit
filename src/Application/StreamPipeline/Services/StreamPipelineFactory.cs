using Application.StreamPipeline.Models;
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

    public StreamPipelineService Pipe(StreamTranceiver streamTranceiver, int bufferSize, CancellationToken stoppingToken)
    {
        var streamPipelineService = _serviceProvider.GetRequiredService<StreamPipelineService>();
        streamPipelineService.Start(streamTranceiver, bufferSize, stoppingToken);
        return streamPipelineService;
    }
}
