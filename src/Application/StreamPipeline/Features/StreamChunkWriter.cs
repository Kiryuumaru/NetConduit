using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Features;

internal class StreamChunkWriter(Stream stream)
{
    private readonly Stream _stream = stream;

    private Memory<byte>? _chunkBytes = null;
    private int? _chunkPosition = null;

    public bool WriteChunk(long length, ReadOnlySpan<byte> buffer)
    {
        if (_chunkBytes == null || _chunkPosition == null)
        {
            if (length == buffer.Length)
            {
                _stream.Write(buffer);
                return true;
            }
            else if (length > buffer.Length)
            {
                _chunkBytes = new byte[length];
                buffer.CopyTo(_chunkBytes.Value.Span[..buffer.Length]);
                _chunkPosition = buffer.Length;
                return false;
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
                return false;
            }
            else if (chunkNextPos == _chunkBytes.Value.Length)
            {
                buffer.CopyTo(_chunkBytes.Value.Span.Slice(_chunkPosition.Value, buffer.Length));
                _stream.Write(_chunkBytes.Value.Span);
                _chunkBytes = null;
                _chunkPosition = null;
                return true;
            }
            else
            {
                var chunkExcess = chunkNextPos - _chunkBytes.Value.Length;
                var chunkLengthToWrite = buffer.Length - chunkExcess;
                buffer[..chunkLengthToWrite].CopyTo(_chunkBytes.Value.Span.Slice(_chunkPosition.Value, chunkLengthToWrite));
                _stream.Write(_chunkBytes.Value.Span);
                _chunkBytes = new byte[length - chunkLengthToWrite];
                buffer.Slice(chunkLengthToWrite, chunkExcess).CopyTo(_chunkBytes.Value.Span[..chunkExcess]);
                _chunkPosition = chunkExcess;
                return true;
            }
        }
    }
}
