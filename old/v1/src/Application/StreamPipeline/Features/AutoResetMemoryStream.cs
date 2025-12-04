using DisposableHelpers.Attributes;
using System.Buffers;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class AutoResetMemoryStream(int capacity) : Stream
{
    private readonly MemoryStream _memoryStream = new(capacity);

    private readonly Lock _lockObj = new();

    private long _readPosition = 0;
    private long _writePosition = 0;

    public override bool CanRead => _memoryStream.CanRead;

    public override bool CanSeek { get; } = false;

    public override bool CanWrite => _memoryStream.CanWrite;

    public override long Length => _memoryStream.Length;

    public override long Position
    {
        get => _memoryStream.Position;
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CoreRead(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CoreRead(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }
        return Task.FromResult(CoreRead(buffer.AsSpan(offset, count)));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
        return ValueTask.FromResult(CoreRead(buffer.Span));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        CoreWrite(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        CoreWrite(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        CoreWrite(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }
        CoreWrite(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected int CoreRead(Span<byte> buffer)
    {
        using var _ = _lockObj.EnterScope();

        _memoryStream.Position = _readPosition;
        int readCount = _memoryStream.Read(buffer);
        _readPosition = _memoryStream.Position;

        if (_readPosition == _writePosition)
        {
            _memoryStream.SetLength(0);
            _memoryStream.Position = 0;
            _readPosition = 0;
            _writePosition = 0;
        }

        return readCount;
    }

    protected void CoreWrite(ReadOnlySpan<byte> buffer)
    {
        using var _ = _lockObj.EnterScope();

        _memoryStream.Position = _writePosition;
        _memoryStream.Write(buffer);
        _writePosition = _memoryStream.Position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memoryStream.Dispose();
        }

        CoreDispose();

        base.Dispose(disposing);
    }
}
