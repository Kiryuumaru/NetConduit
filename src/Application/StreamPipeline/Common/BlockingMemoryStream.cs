using Application.StreamPipeline.Models;
using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

[Disposable]

public partial class BlockingMemoryStream : Stream
{
    private readonly MemoryStream _memoryStream = new();
    private readonly object _lock = new();

    public override bool CanRead => !IsDisposed;

    public override bool CanSeek => false;

    public override bool CanWrite => !IsDisposed;

    public override long Length => _memoryStream.Length;

    public override long Position
    {
        get => _memoryStream.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _memoryStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            while (_memoryStream.Length == 0)
            {
                Monitor.Wait(_lock);
            }

            int bytesRead = _memoryStream.Read(buffer, offset, count);
            return bytesRead;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            _memoryStream.Write(buffer, offset, count);
            Monitor.Pulse(_lock);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        _memoryStream.SetLength(value);
    }
}
