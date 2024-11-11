using DisposableHelpers.Attributes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

[Disposable]
public partial class TranceiverStream(Stream receiverStream, Stream senderStream) : Stream
{
    private readonly Stream _receiverStream = receiverStream;
    private readonly Stream _senderStream = senderStream;

    public override bool CanRead => _receiverStream.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _senderStream.CanWrite;

    public override bool CanTimeout => _receiverStream.CanTimeout || _senderStream.CanTimeout;

    public override int ReadTimeout
    {
        get => _receiverStream.ReadTimeout;
        set => _receiverStream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _senderStream.WriteTimeout;
        set => _senderStream.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Close()
    {
        _receiverStream.Close();
        _senderStream.Close();

        base.Close();
    }

    public override void CopyTo(Stream destination, int bufferSize)
    {
        _receiverStream.CopyTo(destination, bufferSize);
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        return _receiverStream.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    public override void Flush()
    {
        _senderStream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _senderStream.FlushAsync(cancellationToken);
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return _receiverStream.BeginRead(buffer, offset, count, callback, state);
    }

    public override int ReadByte()
    {
        return _receiverStream.ReadByte();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _receiverStream.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        return _receiverStream.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _receiverStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return await _receiverStream.ReadAsync(buffer, cancellationToken);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        return _receiverStream.EndRead(asyncResult);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return _senderStream.BeginWrite(buffer, offset, count, callback, state);
    }

    public override void WriteByte(byte value)
    {
        _senderStream.WriteByte(value);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _senderStream.Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _senderStream.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _senderStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _senderStream.WriteAsync(buffer, cancellationToken);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        _senderStream.EndWrite(asyncResult);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _receiverStream.Dispose();
            _senderStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
