using DisposableHelpers.Attributes;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class TranceiverStream(Stream receiverStream, Stream senderStream) : Stream
{
    public Stream ReceiverStream => receiverStream;

    public Stream SenderStream => senderStream;

    public override bool CanRead => ReceiverStream.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => SenderStream.CanWrite;

    public override bool CanTimeout => ReceiverStream.CanTimeout || SenderStream.CanTimeout;

    public override int ReadTimeout
    {
        get => ReceiverStream.ReadTimeout;
        set => ReceiverStream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => SenderStream.WriteTimeout;
        set => SenderStream.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Close()
    {
        ReceiverStream.Close();
        SenderStream.Close();

        base.Close();
    }

    public override void CopyTo(Stream destination, int bufferSize)
        => ReceiverStream.CopyTo(destination, bufferSize);

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        => ReceiverStream.CopyToAsync(destination, bufferSize, cancellationToken);

    public override void Flush()
        => SenderStream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => SenderStream.FlushAsync(cancellationToken);

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        => ReceiverStream.BeginRead(buffer, offset, count, callback, state);

    public override int ReadByte()
        => ReceiverStream.ReadByte();

    public override int Read(byte[] buffer, int offset, int count)
        => ReceiverStream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer)
        => ReceiverStream.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReceiverStream.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => ReceiverStream.ReadAsync(buffer, cancellationToken);

    public override int EndRead(IAsyncResult asyncResult)
        => ReceiverStream.EndRead(asyncResult);

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        => SenderStream.BeginWrite(buffer, offset, count, callback, state);

    public override void WriteByte(byte value)
        => SenderStream.WriteByte(value);

    public override void Write(ReadOnlySpan<byte> buffer)
        => SenderStream.Write(buffer);

    public override void Write(byte[] buffer, int offset, int count)
        => SenderStream.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => SenderStream.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        => SenderStream.WriteAsync(buffer, cancellationToken);

    public override void EndWrite(IAsyncResult asyncResult)
        => SenderStream.EndWrite(asyncResult);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReceiverStream.Dispose();
            SenderStream.Dispose();
        }

        CoreDispose();

        base.Dispose(disposing);
    }
}
