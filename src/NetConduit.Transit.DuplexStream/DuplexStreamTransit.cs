using NetConduit.Events;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DuplexStream;

/// <summary>
/// A transit that wraps a channel pair (WriteChannel + ReadChannel) as a bidirectional Stream.
/// This allows using a pair of simplex channels as a single duplex stream.
/// </summary>
public sealed class DuplexStreamTransit : Stream, ITransit
{
    private readonly IWriteChannel _writeChannel;
    private readonly IReadChannel _readChannel;
    private volatile bool _disposed;
    private volatile bool _readyFired;
    private readonly object _readyLock = new();

    /// <summary>
    /// Creates a new DuplexStreamTransit from a write channel and read channel pair.
    /// </summary>
    public DuplexStreamTransit(IWriteChannel writeChannel, IReadChannel readChannel)
    {
        _writeChannel = writeChannel ?? throw new ArgumentNullException(nameof(writeChannel));
        _readChannel = readChannel ?? throw new ArgumentNullException(nameof(readChannel));
        SubscribeToChannelEvents();
    }

    private void SubscribeToChannelEvents()
    {
        _writeChannel.Ready += OnChannelReady;
        _writeChannel.Connected += OnChannelConnected;
        _writeChannel.Disconnected += OnChannelDisconnected;
        _readChannel.Ready += OnChannelReady;
        _readChannel.Connected += OnChannelConnected;
        _readChannel.Disconnected += OnChannelDisconnected;
    }

    private void OnChannelReady(object? sender, EventArgs e)
    {
        // Fire Ready only when BOTH channels are ready
        if (!_writeChannel.IsReady || !_readChannel.IsReady) return;
        lock (_readyLock)
        {
            if (_readyFired) return;
            _readyFired = true;
        }
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void OnChannelConnected(object? sender, EventArgs e) => Connected?.Invoke(this, EventArgs.Empty);

    private void OnChannelDisconnected(object? sender, DisconnectedEventArgs e) => Disconnected?.Invoke(this, e);

    /// <inheritdoc/>
    public bool IsReady => !_disposed && _writeChannel.IsReady && _readChannel.IsReady;

    /// <inheritdoc/>
    public bool IsConnected => !_disposed && _writeChannel.IsConnected && _readChannel.IsConnected;

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel.ChannelId;

    /// <inheritdoc/>
    public event EventHandler? Ready;

    /// <inheritdoc/>
    public event EventHandler? Connected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc/>
    public async Task WaitForReadyAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(
            _writeChannel.WaitForReadyAsync(ct),
            _readChannel.WaitForReadyAsync(ct)).ConfigureAwait(false);
    }

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
    public override void Flush() { }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
            UnsubscribeFromChannelEvents();
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

        UnsubscribeFromChannelEvents();

        await _writeChannel.DisposeAsync().ConfigureAwait(false);
        await _readChannel.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void UnsubscribeFromChannelEvents()
    {
        _writeChannel.Ready -= OnChannelReady;
        _writeChannel.Connected -= OnChannelConnected;
        _writeChannel.Disconnected -= OnChannelDisconnected;
        _readChannel.Ready -= OnChannelReady;
        _readChannel.Connected -= OnChannelConnected;
        _readChannel.Disconnected -= OnChannelDisconnected;
    }
}
