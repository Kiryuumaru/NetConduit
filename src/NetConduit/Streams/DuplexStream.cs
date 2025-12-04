namespace NetConduit.Streams;

/// <summary>
/// A bidirectional stream that combines separate read and write streams.
/// Useful for creating transport streams for nested multiplexers.
/// </summary>
public sealed class DuplexStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;
    private readonly bool _ownsStreams;

    /// <summary>
    /// Creates a bidirectional stream from separate read and write streams.
    /// </summary>
    /// <param name="readStream">Stream to read from.</param>
    /// <param name="writeStream">Stream to write to.</param>
    /// <param name="ownsStreams">If true, disposes the underlying streams when this stream is disposed.</param>
    public DuplexStream(Stream readStream, Stream writeStream, bool ownsStreams = false)
    {
        _readStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        _writeStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _ownsStreams = ownsStreams;
        
        if (!_readStream.CanRead)
            throw new ArgumentException("Read stream must be readable.", nameof(readStream));
        if (!_writeStream.CanWrite)
            throw new ArgumentException("Write stream must be writable.", nameof(writeStream));
    }

    /// <summary>
    /// Creates a bidirectional stream from a ReadChannel (for reading) and WriteChannel (for writing).
    /// </summary>
    public static DuplexStream FromChannels(ReadChannel readChannel, WriteChannel writeChannel, bool ownsChannels = false)
    {
        return new DuplexStream(readChannel, writeChannel, ownsChannels);
    }

    /// <inheritdoc/>
    public override bool CanRead => _readStream.CanRead;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => _writeStream.CanWrite;
    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() => _writeStream.Flush();
    
    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writeStream.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
        => _readStream.Read(buffer, offset, count);

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _readStream.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _readStream.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override int ReadByte()
        => _readStream.ReadByte();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => _writeStream.Write(buffer, offset, count);

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _writeStream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _writeStream.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override void WriteByte(byte value)
        => _writeStream.WriteByte(value);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsStreams)
        {
            _readStream.Dispose();
            _writeStream.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_ownsStreams)
        {
            await _readStream.DisposeAsync().ConfigureAwait(false);
            await _writeStream.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
