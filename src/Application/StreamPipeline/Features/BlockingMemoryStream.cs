using DisposableHelpers.Attributes;
using System.Buffers;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class BlockingMemoryStream(int capacity) : MemoryStream(capacity)
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
        int readCount = CoreRead(buffer, offset, count, cancellationToken);
        return Task.FromResult(readCount);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
        int readCount = CoreRead(sharedBuffer, 0, buffer.Length, cancellationToken);
        sharedBuffer.AsSpan().CopyTo(buffer.Span);
        return ValueTask.FromResult(readCount);
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

            using var _ = _lockObj.EnterScope();

            base.Position = _readPosition;
            readCount = base.Read(buffer, offset, count);
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

                break;
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
