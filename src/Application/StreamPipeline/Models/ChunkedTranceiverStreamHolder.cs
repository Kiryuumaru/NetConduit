using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Models;

[Disposable]
internal partial class ChunkedTranceiverStreamHolder(TranceiverStream tranceiverStream)
{
    public readonly TranceiverStream TranceiverStream = tranceiverStream;

    private readonly StreamChunkWriter _streamChunkWriter = new(tranceiverStream.ReceiverStream);

    public void WriteToReceiverStream(long length, ReadOnlySpan<byte> buffer)
    {
        _streamChunkWriter.WriteChunk(length, buffer);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            TranceiverStream.Dispose();
        }
    }
}
