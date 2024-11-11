using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(ILogger<StreamPipelineService> logger) : Dictionary<Guid, StreamTranceiver>
{
    private readonly ILogger<StreamPipelineService> _logger = logger;

    private const int _headerSize = 16;

    private StreamTranceiver? _streamTranceiver = null;
    private CancellationTokenSource? _cts = null;

    public async void Start(StreamTranceiver streamTranceiver, int bufferSize, CancellationToken stoppingToken)
    {
        var fullBufferSize = bufferSize + _headerSize;

        if (_cts != null)
        {
            throw new Exception("StreamTranceiver already started");
        }

        _streamTranceiver = streamTranceiver;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Memory<byte> receivedBytes = new byte[fullBufferSize];
        await _streamTranceiver.ReceiverStream.ReadAsync(receivedBytes, _cts.Token);
        Guid guid = new(receivedBytes[.._headerSize].Span);

        BlockingMemoryStream blockingMemoryStream = new(bufferSize);

        await blockingMemoryStream.WriteAsync(receivedBytes[_headerSize..], _cts.Token);
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();

            _streamTranceiver?.Dispose();

            foreach (var pipe in Values)
            {
                pipe.Dispose();
            }
        }
    }
}
