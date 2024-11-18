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

    private Memory<byte>? _chunkBytes = null;
    private int? _chunkPosition = null;

    public void WriteToReceiverStream(long length, ReadOnlySpan<byte> buffer)
    {
        if (_chunkBytes == null || _chunkPosition == null)
        {
            if (length == buffer.Length)
            {
                TranceiverStream.ReceiverStream.Write(buffer);
            }
            else if (length > buffer.Length)
            {
                _chunkBytes = new byte[length];
                buffer.CopyTo(_chunkBytes.Value.Span[..buffer.Length]);
                _chunkPosition = buffer.Length;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
        }
        else
        {
            var chunkNextPos = _chunkPosition.Value + buffer.Length;

            if (chunkNextPos < _chunkBytes.Value.Length)
            {
                buffer.CopyTo(_chunkBytes.Value.Span.Slice(_chunkPosition.Value, buffer.Length));
                _chunkPosition = chunkNextPos;
            }
            else if (chunkNextPos == _chunkBytes.Value.Length)
            {
                buffer.CopyTo(_chunkBytes.Value.Span.Slice(_chunkPosition.Value, buffer.Length));
                TranceiverStream.ReceiverStream.Write(_chunkBytes.Value.Span);
                _chunkBytes = null;
                _chunkPosition = null;
            }
            else
            {
                var chunkExcess = chunkNextPos - _chunkBytes.Value.Length;
                var chunkLengthToWrite = buffer.Length - chunkExcess;
                buffer[..chunkLengthToWrite].CopyTo(_chunkBytes.Value.Span.Slice(_chunkPosition.Value, chunkLengthToWrite));
                TranceiverStream.ReceiverStream.Write(_chunkBytes.Value.Span);
                _chunkBytes = new byte[length - chunkLengthToWrite];
                buffer.Slice(chunkLengthToWrite, chunkExcess).CopyTo(_chunkBytes.Value.Span[..chunkExcess]);
                _chunkPosition = chunkExcess;
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            TranceiverStream.Dispose();
        }
    }
}
