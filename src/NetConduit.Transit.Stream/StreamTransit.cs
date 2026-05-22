using NetConduit.Events;
using NetConduit.Interfaces;

namespace NetConduit.Transit.Stream;

/// <summary>
/// A transit that wraps a single channel as a simplex (one-way) Stream.
/// For write channels, this provides a write-only stream.
/// For read channels, this provides a read-only stream.
/// </summary>
public sealed class StreamTransit : System.IO.Stream, ITransit
{
    private readonly IWriteChannel? _writeChannel;
    private readonly IReadChannel? _readChannel;
    private volatile bool _disposed;
    private volatile bool _readyFired;
    private readonly object _readyLock = new();

    /// <summary>
    /// Creates a write-only StreamTransit from a WriteChannel.
    /// </summary>
    public StreamTransit(IWriteChannel writeChannel)
    {
        _writeChannel = writeChannel ?? throw new ArgumentNullException(nameof(writeChannel));
        SubscribeToChannelEvents();
        ReplayReadyIfChannelAlreadyReady();
    }

    /// <summary>
    /// Creates a read-only StreamTransit from a ReadChannel.
    /// </summary>
    public StreamTransit(IReadChannel readChannel)
    {
        _readChannel = readChannel ?? throw new ArgumentNullException(nameof(readChannel));
        SubscribeToChannelEvents();
        ReplayReadyIfChannelAlreadyReady();
    }

    // Channel.Ready is single-shot; if the channel was already ready before we
    // subscribed, the event we wired up will never fire. Synthesise the call so
    // subscribers attached after construction still observe Ready exactly once
    // (#266). OnChannelReady's _readyFired guard makes the synthesised call
    // race-safe against a concurrent genuine event.
    private void ReplayReadyIfChannelAlreadyReady()
    {
        if ((_writeChannel?.IsReady ?? false) || (_readChannel?.IsReady ?? false))
            OnChannelReady(this, EventArgs.Empty);
    }

    private void SubscribeToChannelEvents()
    {
        if (_writeChannel is not null)
        {
            _writeChannel.Ready += OnChannelReady;
            _writeChannel.Connected += OnChannelConnected;
            _writeChannel.Disconnected += OnChannelDisconnected;
        }
        if (_readChannel is not null)
        {
            _readChannel.Ready += OnChannelReady;
            _readChannel.Connected += OnChannelConnected;
            _readChannel.Disconnected += OnChannelDisconnected;
        }
    }

    private void OnChannelReady(object? sender, EventArgs e)
    {
        EventHandler? handlers;
        lock (_readyLock)
        {
            if (_readyFired) return;
            _readyFired = true;
            handlers = _readyHandlers;
        }
        handlers?.Invoke(this, EventArgs.Empty);
    }

    private void OnChannelConnected(object? sender, EventArgs e) => Connected?.Invoke(this, EventArgs.Empty);

    private void OnChannelDisconnected(object? sender, DisconnectedEventArgs e) => Disconnected?.Invoke(this, e);

    /// <inheritdoc/>
    public bool IsReady => !_disposed && (_writeChannel?.IsReady ?? _readChannel?.IsReady ?? false);

    /// <inheritdoc/>
    public bool IsConnected => !_disposed && (_writeChannel?.IsConnected ?? _readChannel?.IsConnected ?? false);

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel?.ChannelId;

    private EventHandler? _readyHandlers;

    /// <inheritdoc/>
    /// <remarks>
    /// Latching: subscribers attached after the transit has already become Ready are
    /// invoked immediately on subscription, so callers that wait for channel readiness
    /// before constructing the transit still observe the event exactly once (#266).
    /// </remarks>
    public event EventHandler? Ready
    {
        add
        {
            if (value is null) return;
            bool fireImmediately;
            lock (_readyLock)
            {
                _readyHandlers += value;
                fireImmediately = _readyFired;
            }
            if (fireImmediately) value(this, EventArgs.Empty);
        }
        remove
        {
            lock (_readyLock) { _readyHandlers -= value; }
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Connected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc/>
    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        if (_writeChannel is not null)
            return _writeChannel.WaitForReadyAsync(ct);
        if (_readChannel is not null)
            return _readChannel.WaitForReadyAsync(ct);
        return Task.CompletedTask;
    }

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
    public override void Flush() { }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
            UnsubscribeFromChannelEvents();
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

        UnsubscribeFromChannelEvents();

        if (_writeChannel is not null)
            await _writeChannel.DisposeAsync().ConfigureAwait(false);

        if (_readChannel is not null)
            await _readChannel.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void UnsubscribeFromChannelEvents()
    {
        if (_writeChannel is not null)
        {
            _writeChannel.Ready -= OnChannelReady;
            _writeChannel.Connected -= OnChannelConnected;
            _writeChannel.Disconnected -= OnChannelDisconnected;
        }
        if (_readChannel is not null)
        {
            _readChannel.Ready -= OnChannelReady;
            _readChannel.Connected -= OnChannelConnected;
            _readChannel.Disconnected -= OnChannelDisconnected;
        }
    }
}
