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
    // Connected/Disconnected edge latches for the AND-Connected / OR-Disconnected
    // / latch-reset pattern. Mirrors the DuplexStreamTransit fix
    // and MessageTransit fix. Required when both write+read channels
    // are configured so the transit doesn't fire Connected/Disconnected twice
    // per cycle and so reconnect cycles re-fire events.
    private readonly object _stateLock = new();
    private bool _connectedFired;
    private bool _disconnectedFired;

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
    // . OnChannelReady's _readyFired guard makes the synthesised call
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

    // When both write+read channels are configured each half raises
    // its own Connected/Disconnected events; forwarding each independently
    // double-fired the transit's events. AND-coalesce Connected (all halves
    // up), OR-coalesce Disconnected (first half down). Reset opposite latch
    // on each edge so reconnect cycles fire.
    private void OnChannelConnected(object? sender, EventArgs e)
    {
        if ((_writeChannel?.IsConnected ?? true) == false) return;
        if ((_readChannel?.IsConnected ?? true) == false) return;
        bool fire = false;
        lock (_stateLock)
        {
            if (_connectedFired) return;
            _connectedFired = true;
            _disconnectedFired = false;
            fire = true;
        }
        if (fire) Connected?.Invoke(this, EventArgs.Empty);
    }

    private void OnChannelDisconnected(object? sender, DisconnectedEventArgs e)
    {
        bool fire = false;
        lock (_stateLock)
        {
            if (_disconnectedFired) return;
            _disconnectedFired = true;
            _connectedFired = false;
            fire = true;
        }
        if (fire) Disconnected?.Invoke(this, e);
    }

    /// <inheritdoc/>
    // When both write+read channels are configured, IsReady must be the
    // AND of both halves, not the first non-null half. The old expression
    // `(_writeChannel?.IsReady ?? _readChannel?.IsReady ?? false)` reported
    // ready as soon as the write half was up, ignoring the read half
    // entirely.
    public bool IsReady
    {
        get
        {
            if (_disposed) return false;
            var writeReady = _writeChannel?.IsReady ?? true;
            var readReady = _readChannel?.IsReady ?? true;
            // At least one half must be configured for IsReady to be true.
            if (_writeChannel is null && _readChannel is null) return false;
            return writeReady && readReady;
        }
    }

    /// <inheritdoc/>
    public bool IsConnected
    {
        get
        {
            if (_disposed) return false;
            var writeConnected = _writeChannel?.IsConnected ?? true;
            var readConnected = _readChannel?.IsConnected ?? true;
            if (_writeChannel is null && _readChannel is null) return false;
            return writeConnected && readConnected;
        }
    }

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel?.ChannelId;

    private EventHandler? _readyHandlers;

    /// <inheritdoc/>
    /// <remarks>
    /// Latching: subscribers attached after the transit has already become Ready are
    /// invoked immediately on subscription, so callers that wait for channel readiness
    /// before constructing the transit still observe the event exactly once.
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
    // When both halves are configured, callers must wait for BOTH to
    // become ready, not just the first non-null one.
    public async Task WaitForReadyAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task>(2);
        if (_writeChannel is not null) tasks.Add(_writeChannel.WaitForReadyAsync(ct));
        if (_readChannel is not null) tasks.Add(_readChannel.WaitForReadyAsync(ct));
        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
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

            // Aggregate inner-dispose failures so a throw from one half
            // doesn't strand the other half (leak its slab back to the pool)
            // and doesn't skip base.Dispose. Mirrors the DuplexStreamTransit
            // pattern.
            List<Exception>? errors = null;
            if (_writeChannel is not null)
            {
                try { _writeChannel.Dispose(); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            }
            if (_readChannel is not null)
            {
                try { _readChannel.Dispose(); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            }

            base.Dispose(disposing);

            if (errors is { Count: 1 }) throw errors[0];
            if (errors is { Count: > 1 }) throw new AggregateException(errors);
            return;
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

        // Aggregate inner-dispose failures so a throw from one half
        // doesn't strand the other half (leak its slab back to the pool)
        // and doesn't skip base.DisposeAsync. Mirrors the DuplexStreamTransit
        // pattern.
        List<Exception>? errors = null;
        if (_writeChannel is not null)
        {
            try { await _writeChannel.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (_readChannel is not null)
        {
            try { await _readChannel.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }

        await base.DisposeAsync().ConfigureAwait(false);

        if (errors is { Count: 1 }) throw errors[0];
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
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
