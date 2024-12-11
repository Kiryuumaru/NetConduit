using Application.Common.Extensions;
using Application.StreamPipeline.Common;
using System.Buffers;

namespace Application.StreamPipeline.Features;

public partial class BlockingMemoryStream1(int capacity) : AutoResetMemoryStream(capacity)
{
    private readonly ManualResetEventSlim _dataReady = new(false);

    public override void Write(byte[] buffer, int offset, int count)
        => CoreWrite(buffer, offset, count);

    public override int Read(byte[] buffer, int offset, int count)
        => CoreRead(buffer, offset, count, default);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => CoreReadAsync(buffer, offset, count, cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        int readCount = await CoreReadAsync(sharedBuffer, 0, buffer.Length, cancellationToken);
        sharedBuffer.AsSpan().CopyTo(buffer.Span);
        return readCount;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _dataReady.Set();
                _dataReady.Dispose();
            }
            catch { }
        }

        base.Dispose(disposing);
    }

    public int CoreRead(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int readCount = 0;

        ObjectDisposedException.ThrowIf(IsDisposedOrDisposing, this);

        while (readCount == 0)
        {
            _dataReady.Wait(cancellationToken);

            if (IsDisposedOrDisposing)
            {
                break;
            }

            readCount = base.Read(buffer, offset, count);

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

    public async ValueTask<int> CoreReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int readCount = 0;

        ObjectDisposedException.ThrowIf(IsDisposedOrDisposing, this);

        while (readCount == 0)
        {
            if (!_dataReady.WaitHandle.WaitOne(0))
            {
                await _dataReady.WaitHandle.WaitHandleTask(cancellationToken);
            }

            if (IsDisposedOrDisposing)
            {
                break;
            }

            readCount = base.Read(buffer, offset, count);

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

    public void CoreWrite(byte[] buffer, int offset, int count)
    {
        base.Write(buffer, offset, count);
        _dataReady.Set();
    }
}
