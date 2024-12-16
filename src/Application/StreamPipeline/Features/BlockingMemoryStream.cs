using Application.Common.Extensions;
using Application.Common.Features;
using Application.StreamPipeline.Common;
using System;
using System.Buffers;
using System.Threading;

namespace Application.StreamPipeline.Features;

public partial class BlockingMemoryStream(int capacity) : AutoResetMemoryStream(capacity)
{
    private readonly AsyncManualResetEvent _dataReady = new(false);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return BlockingRead(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return BlockingRead(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }
        return BlockingReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
        return BlockingReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        BlockingWrite(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        BlockingWrite(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        BlockingWrite(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }
        BlockingWrite(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected int BlockingRead(Span<byte> buffer)
    {
        int readCount = 0;

        while (readCount == 0 && !IsDisposed)
        {
            _dataReady.Wait();

            if (IsDisposed)
            {
                break;
            }

            readCount = CoreRead(buffer);

            if (readCount == 0)
            {
                _dataReady.Reset();
            }
            else
            {
                break;
            }
        }

        return readCount;
    }

    protected async ValueTask<int> BlockingReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int readCount = 0;

        while (readCount == 0 && !cancellationToken.IsCancellationRequested && !IsDisposed)
        {
            await _dataReady.WaitAsync(cancellationToken);

            if (IsDisposed)
            {
                break;
            }

            readCount = CoreRead(buffer.Span);

            if (readCount == 0)
            {
                _dataReady.Reset();
            }
            else
            {
                break;
            }
        }

        return readCount;
    }

    protected void BlockingWrite(ReadOnlySpan<byte> buffer)
    {
        CoreWrite(buffer);
        _dataReady.Set();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _dataReady.Dispose();
        }
    }
}
