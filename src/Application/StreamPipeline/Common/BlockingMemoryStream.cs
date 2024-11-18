using DisposableHelpers.Attributes;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class BlockingMemoryStream : MemoryStream
{
    private readonly ManualResetEventSlim _dataReady = new(false);
    private readonly object _lockObj = new();

    private long _readPosition = 0;
    private long _writePosition = 0;

    public override bool CanSeek => false;

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }

    public BlockingMemoryStream()
        : base() { }

    public BlockingMemoryStream(int capacity)
        : base(capacity) { }

    public BlockingMemoryStream(byte[] buffer)
        : base(buffer) { }

    public BlockingMemoryStream(byte[] buffer, bool writable)
        : base(buffer, writable) { }

    public BlockingMemoryStream(byte[] buffer, int index, int count)
        : base(buffer, index, count) { }

    public BlockingMemoryStream(byte[] buffer, int index, int count, bool writable)
        : base(buffer, index, count, writable) { }

    public BlockingMemoryStream(byte[] buffer, int index, int count, bool writable, bool publiclyVisible)
        : base(buffer, index, count, writable, publiclyVisible) { }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_lockObj)
        {
            base.Position = _writePosition;
            base.Write(buffer, offset, count);
            _writePosition = base.Position;
            _dataReady.Set();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int readCount = 0;

        ObjectDisposedException.ThrowIf(IsDisposed, this);

        while (readCount == 0)
        {
            _dataReady.Wait();

            if (IsDisposed)
            {
                break;
            }

            lock (_lockObj)
            {
                base.Position = _readPosition;
                readCount = base.Read(buffer, offset, count);
                _readPosition = base.Position;
                if (readCount == 0)
                {
                    _dataReady.Reset();
                }
                else
                {
                    break;
                }
            }
        }

        return readCount;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int readCount = 0;

        ObjectDisposedException.ThrowIf(IsDisposed, this);

        while (readCount == 0)
        {
            _dataReady.Wait(cancellationToken);

            if (IsDisposed)
            {
                break;
            }

            lock (_lockObj)
            {
                base.Position = _readPosition;
                readCount = base.Read(buffer, offset, count);
                _readPosition = base.Position;
                if (readCount == 0)
                {
                    _dataReady.Reset();
                }
                else
                {
                    break;
                }
            }
        }

        return Task.FromResult(readCount);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int readCount = 0;

        ObjectDisposedException.ThrowIf(IsDisposed, this);

        while (readCount == 0)
        {
            _dataReady.Wait(cancellationToken);

            if (IsDisposed)
            {
                break;
            }

            lock (_lockObj)
            {
                base.Position = _readPosition;
                byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                readCount = base.Read(sharedBuffer, 0, buffer.Length);
                sharedBuffer.AsSpan().CopyTo(buffer.Span);
                _readPosition = base.Position;
                if (readCount == 0)
                {
                    _dataReady.Reset();
                }
                else
                {
                    break;
                }
            }
        }

        return ValueTask.FromResult(readCount);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                lock (_lockObj)
                {
                    _dataReady.Set();
                    _dataReady.Dispose();
                }
            }
            catch { }
        }

        CoreDispose();

        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();
}
