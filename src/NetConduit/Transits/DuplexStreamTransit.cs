namespace NetConduit.Transits;

/// <summary>
/// A transit that wraps a channel pair (WriteChannel + ReadChannel) as a bidirectional Stream.
/// This allows using a pair of simplex channels as a single duplex stream.
/// </summary>
public sealed class DuplexStreamTransit : Stream, ITransit
{
    private readonly WriteChannel _writeChannel;
    private readonly ReadChannel _readChannel;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new DuplexStreamTransit from a write channel and read channel pair.
    /// </summary>
    /// <param name="writeChannel">The channel for writing (outgoing data).</param>
    /// <param name="readChannel">The channel for reading (incoming data).</param>
    public DuplexStreamTransit(WriteChannel writeChannel, ReadChannel readChannel)
    {
        _writeChannel = writeChannel ?? throw new ArgumentNullException(nameof(writeChannel));
        _readChannel = readChannel ?? throw new ArgumentNullException(nameof(readChannel));
    }

    /// <inheritdoc/>
    public bool IsConnected => !_disposed &&
        (_writeChannel.State == ChannelState.Open || _readChannel.State == ChannelState.Open);

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel.ChannelId;

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException("DuplexStreamTransit does not support Length.");

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException("DuplexStreamTransit does not support Position.");
        set => throw new NotSupportedException("DuplexStreamTransit does not support Position.");
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
        throw new NotSupportedException("DuplexStreamTransit does not support seeking.");
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException("DuplexStreamTransit does not support SetLength.");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
        {
            _writeChannel.Dispose();
            _readChannel.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _writeChannel.DisposeAsync().ConfigureAwait(false);
        await _readChannel.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Extension methods for creating DuplexStreamTransit instances from multiplexer channels.
/// </summary>
public static class DuplexStreamTransitExtensions
{
    /// <summary>
    /// Creates a bidirectional stream from a write channel and read channel pair.
    /// </summary>
    public static DuplexStreamTransit AsDuplexStream(this WriteChannel writeChannel, ReadChannel readChannel)
    {
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Creates a bidirectional stream from a read channel and write channel pair.
    /// </summary>
    public static DuplexStreamTransit AsDuplexStream(this ReadChannel readChannel, WriteChannel writeChannel)
    {
        return new DuplexStreamTransit(writeChannel, readChannel);
    }
}
