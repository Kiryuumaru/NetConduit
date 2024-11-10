using Application.StreamPipeline.Models;
using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class StreamMessenger
{
    private readonly StreamPipe _streamPipe;
    private readonly CancellationToken _stoppingToken;

    internal StreamMessenger(StreamPipe streamPipe)
    {
        _streamPipe = streamPipe;
        _stoppingToken = CancelWhenDisposing();
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamPipe.Dispose();
        }
    }

    //public async Task<byte[]> Send(byte[] message, CancellationToken cancellationToken)
    //{
    //    var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken, cancellationToken);

    //}
}
