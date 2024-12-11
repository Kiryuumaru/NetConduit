using Application.Common.Extensions;
using DisposableHelpers.Attributes;
using System.Buffers;
using System.Threading;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class BlockingMemoryStreamNew(int capacity) : MemoryStream(capacity)
{
    private readonly ManualResetEventSlim _dataReady = new(false);
    private readonly Lock _lockObj = new();

    private long _readPosition = 0;
    private long _writePosition = 0;

    public override bool CanSeek => false;

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        CoreWrite(buffer, offset, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return CoreRead(buffer, offset, count, default);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return CoreReadAsync(buffer, offset, count, cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        int readCount = await CoreReadAsync(sharedBuffer, 0, buffer.Length, cancellationToken);
        sharedBuffer.AsSpan().CopyTo(buffer.Span);
        return readCount;
    }

    protected override void Dispose(bool disposing)
    {
        CoreDispose();

        if (disposing)
        {
            try
            {
                using var _ = _lockObj.EnterScope();
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

            readCount = NonBlockRead(buffer, offset, count);

            if (readCount != 0)
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

            readCount = NonBlockRead(buffer, offset, count);

            if (readCount != 0)
            {
                break;
            }
        }

        return readCount;
    }

    public int NonBlockRead(byte[] buffer, int offset, int count)
    {
        using var _ = _lockObj.EnterScope();

        base.Position = _readPosition;
        int readCount = base.Read(buffer, offset, count);
        _readPosition = base.Position;

        if (readCount == 0)
        {
            _dataReady.Reset();
        }
        else
        {
            if (_readPosition == _writePosition)
            {
                base.SetLength(0);
                base.Position = 0;
                _readPosition = 0;
                _writePosition = 0;
            }
        }

        return readCount;
    }

    public void CoreWrite(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposedOrDisposing, this);

        using var _ = _lockObj.EnterScope();

        base.Position = _writePosition;
        base.Write(buffer, offset, count);
        _writePosition = base.Position;
        _dataReady.Set();
    }
}
