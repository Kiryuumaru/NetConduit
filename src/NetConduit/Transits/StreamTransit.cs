namespace NetConduit.Transits;

/// <summary>
/// A transit that wraps a single channel as a simplex (one-way) Stream.
/// For write channels, this provides a write-only stream.
/// For read channels, this provides a read-only stream.
/// </summary>
public sealed class StreamTransit : Stream, ITransit
{
    private readonly WriteChannel? _writeChannel;
    private readonly ReadChannel? _readChannel;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a write-only StreamTransit from a WriteChannel.
    /// </summary>
    /// <param name="writeChannel">The write channel to wrap.</param>
    public StreamTransit(WriteChannel writeChannel)
    {
        _writeChannel = writeChannel ?? throw new ArgumentNullException(nameof(writeChannel));
    }

    /// <summary>
    /// Creates a read-only StreamTransit from a ReadChannel.
    /// </summary>
    /// <param name="readChannel">The read channel to wrap.</param>
    public StreamTransit(ReadChannel readChannel)
    {
        _readChannel = readChannel ?? throw new ArgumentNullException(nameof(readChannel));
    }

    /// <inheritdoc/>
    public bool IsConnected => !_disposed &&
        (_writeChannel?.State == ChannelState.Open || _readChannel?.State == ChannelState.Open);

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel?.ChannelId;

    /// <inheritdoc/>
    public override bool CanRead => _readChannel is not null && !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => _writeChannel is not null && !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException("StreamTransit does not support Length.");

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException("StreamTransit does not support Position.");
        set => throw new NotSupportedException("StreamTransit does not support Position.");
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readChannel is null)
            throw new InvalidOperationException("This transit does not support reading.");

        return await _readChannel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeChannel is null)
            throw new InvalidOperationException("This transit does not support writing.");

        await _writeChannel.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // No-op - channels don't buffer
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        // No-op - channels don't buffer
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("StreamTransit does not support seeking.");
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException("StreamTransit does not support SetLength.");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
        {
            _writeChannel?.Dispose();
            _readChannel?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_writeChannel is not null)
            await _writeChannel.DisposeAsync().ConfigureAwait(false);

        if (_readChannel is not null)
            await _readChannel.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Extension methods for creating StreamTransit instances from multiplexer channels.
/// </summary>
public static class StreamTransitExtensions
{
    /// <summary>
    /// Wraps a WriteChannel as a write-only Stream.
    /// </summary>
    public static StreamTransit AsStream(this WriteChannel writeChannel)
    {
        return new StreamTransit(writeChannel);
    }

    /// <summary>
    /// Wraps a ReadChannel as a read-only Stream.
    /// </summary>
    public static StreamTransit AsStream(this ReadChannel readChannel)
    {
        return new StreamTransit(readChannel);
    }
}
