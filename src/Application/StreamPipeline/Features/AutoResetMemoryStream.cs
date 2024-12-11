using DisposableHelpers.Attributes;
using System.Buffers;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class AutoResetMemoryStream(int capacity) : MemoryStream(capacity)
{
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
        ObjectDisposedException.ThrowIf(IsDisposedOrDisposing, this);

        using var _ = _lockObj.EnterScope();

        base.Position = _writePosition;
        base.Write(buffer, offset, count);
        _writePosition = base.Position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposedOrDisposing, this);

        using var _ = _lockObj.EnterScope();

        base.Position = _readPosition;
        int readCount = base.Read(buffer, offset, count);
        _readPosition = base.Position;

        if (_readPosition == _writePosition)
        {
            base.SetLength(0);
            base.Position = 0;
            _readPosition = 0;
            _writePosition = 0;
        }

        return readCount;
    }

    protected override void Dispose(bool disposing)
    {
        CoreDispose();

        base.Dispose(disposing);
    }
}
