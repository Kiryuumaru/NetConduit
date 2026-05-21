using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Events;
using NetConduit.Interfaces;

namespace NetConduit.Transit.Message;

/// <summary>
/// A transit that sends and receives discrete JSON-serialized messages over a channel pair.
/// Uses length-prefixed framing (4-byte big-endian length prefix) for message boundaries.
/// Fully AOT-compatible when using JsonTypeInfo overloads.
/// </summary>
/// <typeparam name="TSend">The type of messages to send.</typeparam>
/// <typeparam name="TReceive">The type of messages to receive.</typeparam>
public sealed class MessageTransit<TSend, TReceive> : ITransit
{
    private readonly IWriteChannel? _writeChannel;
    private readonly IReadChannel? _readChannel;
    private readonly JsonTypeInfo<TSend>? _sendTypeInfo;
    private readonly JsonTypeInfo<TReceive>? _receiveTypeInfo;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly int _maxMessageSize;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private volatile bool _disposed;
    private volatile bool _readyFired;
    // Set once when the inbound channel reaches EOF. ReceiveAllAsync consults
    // this instead of `message is null`, which is always false for non-nullable
    // value-type TReceive and would otherwise yield default(TReceive) forever
    // after the channel closes (issue #177).
    private volatile bool _receiveEof;
    private readonly object _readyLock = new();

    // Per-frame receive state. Survives a cancellation between the length-prefix
    // read and the payload read so the underlying channel never sees a partial
    // frame consume that would desync the next call's length prefix (#240).
    // All access is serialized through _receiveLock.
    private readonly byte[] _pendingLengthBytes = new byte[4];
    private int _pendingLengthOffset;
    private byte[]? _pendingPayloadBuffer;
    private int _pendingPayloadOffset;
    private int _pendingPayloadLength;

    // Number of bytes still owed to an over-max message body that we already
    // committed to consuming from the channel (length prefix was read and
    // validated as too large). These bytes MUST be drained from the channel
    // before the next length prefix can be parsed, or the framing desyncs
    // permanently (#278). Survives cancellation: a cancelled drain resumes
    // on the next ReceiveAsync call. uint covers the full 4-byte length range.
    private uint _pendingDrainRemaining;

    /// <summary>
    /// Creates a new MessageTransit with both send and receive capabilities using AOT-safe JsonTypeInfo.
    /// </summary>
    public MessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonTypeInfo<TSend>? sendTypeInfo,
        JsonTypeInfo<TReceive>? receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        _writeChannel = writeChannel;
        _readChannel = readChannel;
        _sendTypeInfo = sendTypeInfo;
        _receiveTypeInfo = receiveTypeInfo;
        _maxMessageSize = maxMessageSize;
        SubscribeToChannelEvents();
    }

    /// <summary>
    /// Creates a new MessageTransit with both send and receive capabilities using JsonSerializerOptions.
    /// Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public MessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        _writeChannel = writeChannel;
        _readChannel = readChannel;
        _jsonOptions = jsonOptions;
        _maxMessageSize = maxMessageSize;
        SubscribeToChannelEvents();
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
        // Fire Ready only when all configured channels are ready
        var writeReady = _writeChannel?.IsReady ?? true;
        var readReady = _readChannel?.IsReady ?? true;
        if (!writeReady || !readReady) return;
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
    public bool IsReady
    {
        get
        {
            if (_disposed) return false;
            var writeReady = _writeChannel?.IsReady ?? true;
            var readReady = _readChannel?.IsReady ?? true;
            return writeReady && readReady;
        }
    }

    /// <inheritdoc/>
    public bool IsConnected => !_disposed &&
        ((_writeChannel?.IsConnected ?? false) || (_readChannel?.IsConnected ?? false));

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel?.ChannelId;

    /// <inheritdoc/>
    public event EventHandler? Ready;

    /// <inheritdoc/>
    public event EventHandler? Connected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc/>
    public async Task WaitForReadyAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task>(2);
        if (_writeChannel is not null)
            tasks.Add(_writeChannel.WaitForReadyAsync(ct));
        if (_readChannel is not null)
            tasks.Add(_readChannel.WaitForReadyAsync(ct));
        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _sendTypeInfo is null, meaning the non-AOT constructor was used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _sendTypeInfo is null, meaning the non-AOT constructor was used")]
    public async ValueTask SendAsync(TSend message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeChannel is null)
            throw new InvalidOperationException("This transit does not have a write channel configured.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] jsonBytes;
            if (_sendTypeInfo is not null)
            {
                jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message, _sendTypeInfo);
            }
            else
            {
                jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
            }

            if (jsonBytes.Length > _maxMessageSize)
                throw new InvalidOperationException($"Message size ({jsonBytes.Length} bytes) exceeds maximum allowed ({_maxMessageSize} bytes).");

            var totalLength = 4 + jsonBytes.Length;
            var combinedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
            try
            {
                BinaryPrimitives.WriteUInt32BigEndian(combinedBuffer, (uint)jsonBytes.Length);
                jsonBytes.CopyTo(combinedBuffer, 4);
                await _writeChannel.WriteAsync(combinedBuffer.AsMemory(0, totalLength), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(combinedBuffer);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _receiveTypeInfo is null, meaning the non-AOT constructor was used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _receiveTypeInfo is null, meaning the non-AOT constructor was used")]
    public async ValueTask<TReceive?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readChannel is null)
            throw new InvalidOperationException("This transit does not have a read channel configured.");

        await _receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Drain any payload bytes still owed to a previously-rejected
            // over-max message so the next length prefix lands on a clean
            // frame boundary (#278). If cancelled mid-drain, _pendingDrainRemaining
            // preserves progress for the next call.
            if (_pendingDrainRemaining > 0)
            {
                await DrainPendingAsync(cancellationToken).ConfigureAwait(false);
            }

            // Read the 4-byte length prefix into the persistent buffer. If a
            // prior ReceiveAsync was cancelled mid-prefix, _pendingLengthOffset
            // preserves how many bytes were already consumed so this call
            // resumes at the exact same byte boundary — preserving framing
            // invariants across cancellation events (#240).
            while (_pendingPayloadBuffer is null && _pendingLengthOffset < 4)
            {
                var n = await _readChannel.ReadAsync(
                    _pendingLengthBytes.AsMemory(_pendingLengthOffset, 4 - _pendingLengthOffset),
                    cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    if (_pendingLengthOffset == 0)
                    {
                        _receiveEof = true;
                        return default;
                    }
                    _pendingLengthOffset = 0;
                    throw new EndOfStreamException("Unexpected end of stream while reading message length prefix.");
                }
                _pendingLengthOffset += n;
            }

            // Length prefix complete: allocate (or reuse) the payload buffer.
            if (_pendingPayloadBuffer is null)
            {
                var messageLength = BinaryPrimitives.ReadUInt32BigEndian(_pendingLengthBytes);

                if (messageLength > (uint)_maxMessageSize)
                {
                    // Mark the over-max payload bytes for drain on the next
                    // ReceiveAsync so framing resumes on a clean boundary
                    // instead of parsing payload bytes as the next length
                    // prefix (#278). Clear the length-prefix progress; this
                    // length has been fully consumed.
                    _pendingLengthOffset = 0;
                    _pendingDrainRemaining = messageLength;
                    throw new InvalidOperationException(
                        $"Received message size ({messageLength} bytes) exceeds maximum allowed ({_maxMessageSize} bytes); the oversized payload will be discarded and the next ReceiveAsync call will return the following message.");
                }

                if (messageLength == 0)
                {
                    _pendingLengthOffset = 0;
                    throw new InvalidOperationException("Received a message with zero-length payload.");
                }

                _pendingPayloadLength = (int)messageLength;
                _pendingPayloadBuffer = ArrayPool<byte>.Shared.Rent(_pendingPayloadLength);
                _pendingPayloadOffset = 0;
            }

            // Read the payload. If cancelled mid-payload, _pendingPayloadOffset
            // preserves the progress so the next call resumes exactly here.
            while (_pendingPayloadOffset < _pendingPayloadLength)
            {
                var n = await _readChannel.ReadAsync(
                    _pendingPayloadBuffer.AsMemory(_pendingPayloadOffset, _pendingPayloadLength - _pendingPayloadOffset),
                    cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    var torn = _pendingPayloadBuffer;
                    _pendingPayloadBuffer = null;
                    _pendingPayloadOffset = 0;
                    _pendingPayloadLength = 0;
                    _pendingLengthOffset = 0;
                    ArrayPool<byte>.Shared.Return(torn);
                    throw new EndOfStreamException("Unexpected end of stream while reading message payload.");
                }
                _pendingPayloadOffset += n;
            }

            // Frame complete — capture, clear state, then deserialize.
            var payload = _pendingPayloadBuffer;
            var payloadLength = _pendingPayloadLength;
            _pendingPayloadBuffer = null;
            _pendingPayloadOffset = 0;
            _pendingPayloadLength = 0;
            _pendingLengthOffset = 0;

            try
            {
                if (_receiveTypeInfo is not null)
                {
                    return JsonSerializer.Deserialize(payload.AsSpan(0, payloadLength), _receiveTypeInfo);
                }
                else
                {
                    return JsonSerializer.Deserialize<TReceive>(payload.AsSpan(0, payloadLength), _jsonOptions);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    /// <summary>
    /// Reads and discards <see cref="_pendingDrainRemaining"/> bytes from the
    /// inbound channel. Caller MUST hold <see cref="_receiveLock"/>. On
    /// cancellation the remaining count is preserved so the next call resumes
    /// the drain. On premature EOF an <see cref="EndOfStreamException"/> is
    /// raised — there is no clean recovery once the channel closes mid-drain.
    /// </summary>
    private async ValueTask DrainPendingAsync(CancellationToken cancellationToken)
    {
        const int DrainChunk = 8192;
        var buf = ArrayPool<byte>.Shared.Rent(DrainChunk);
        try
        {
            while (_pendingDrainRemaining > 0)
            {
                var toRead = (int)Math.Min((uint)buf.Length, _pendingDrainRemaining);
                var n = await _readChannel!.ReadAsync(buf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    // Channel closed before the over-max payload finished
                    // draining. The framing is unrecoverable; signal EOF.
                    _pendingDrainRemaining = 0;
                    _receiveEof = true;
                    throw new EndOfStreamException("Unexpected end of stream while discarding oversized message payload.");
                }
                _pendingDrainRemaining -= (uint)n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TReceive> ReceiveAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            TReceive? message;
            try
            {
                message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            // EOF is signalled exclusively via `_receiveEof`. Using
            // `message is null` as a secondary terminator would conflate
            // genuine end-of-stream with a peer that legitimately sends a
            // JSON `null` payload (issue #220) — losing that message and
            // every subsequent one. The flag is set inside ReceiveAsync
            // when the length-prefix or payload read returns 0 bytes.
            if (_receiveEof)
                yield break;

            yield return message!;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        UnsubscribeFromChannelEvents();

        // Aggregate per-step failures so a throw from one step does not skip the rest
        // and leak the read channel slab, the pending payload buffer, or the lock
        // semaphores (#292, mirroring StreamPair PR #224 / DuplexStreamTransit #305).
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
        try { ReturnPendingPayloadBuffer(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _sendLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _receiveLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }

        if (errors is { Count: 1 }) throw errors[0];
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        UnsubscribeFromChannelEvents();

        // Aggregate per-step failures (#292).
        List<Exception>? errors = null;
        try { _writeChannel?.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _readChannel?.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { ReturnPendingPayloadBuffer(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _sendLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _receiveLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }

        if (errors is { Count: 1 }) throw errors[0];
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
    }

    // Returns any payload buffer held across a cancelled mid-frame ReceiveAsync
    // back to the array pool so dispose does not leak it.
    private void ReturnPendingPayloadBuffer()
    {
        var buf = _pendingPayloadBuffer;
        if (buf is null) return;
        _pendingPayloadBuffer = null;
        _pendingPayloadOffset = 0;
        _pendingPayloadLength = 0;
        _pendingLengthOffset = 0;
        ArrayPool<byte>.Shared.Return(buf);
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
