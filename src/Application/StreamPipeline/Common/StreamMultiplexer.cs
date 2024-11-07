using Application.StreamPipeline.Models;
using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

[Disposable]
internal partial class StreamMultiplexer(StreamPipe streamPipe)
{
    private readonly StreamPipe _streamPipe = streamPipe;    

    private readonly Dictionary<int, StreamPipe> _inputStreamPipes = [];

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamPipe.Dispose();

            foreach (var streamPipe in _inputStreamPipes.Values)
            {
                streamPipe.Dispose();
            }
        }
    }
}
