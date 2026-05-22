using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;
using NetConduit.Transit.DeltaMessage.Internal;

namespace NetConduit.Transit.DeltaMessage;

/// <summary>
/// A transit that sends and receives state changes as deltas, minimizing bandwidth.
/// Only changed properties are transmitted after the first full state sync.
/// Thread-safe and optimized for high-frequency payloads.
/// </summary>
/// <typeparam name="T">The type of state to synchronize.</typeparam>
public sealed class DeltaMessageTransit<T> : IAsyncDisposable
{
    private readonly IWriteChannel? _writeChannel;
    private readonly IReadChannel? _readChannel;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly int _maxMessageSize;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);

    private JsonNode? _lastSentState;
    private JsonNode? _lastReceivedState;
    // Set by the receive path when a peer 0x02 resync request arrives, consumed by the
    // send path under _sendLock at the top of every SendCore/SendBatchCore call. The
    // receive path must not mutate _lastSentState directly: that field is owned by the
    // sender lock, and a cross-lock null write races with ComputeDelta and produces
    // either NRE or silent state divergence (issue #300).
    private int _resyncRequested;
    // Set by the receive path when WE need to ask the peer to re-send full state
    // (delta arrived before any full state, or ApplyDelta threw mid-batch).
    // Drained by the ReceiveAsync wrapper AFTER _receiveLock is released, then
    // emitted under _sendLock so the 5-byte resync frame cannot interleave bytes
    // with a concurrent SendAsync's in-flight write (issue #193). Writing from
    // inside _receiveLock would either bypass _sendLock (corrupts wire framing)
    // or, if acquired in-place, invert the lock order against ResetState
    // (_sendLock then _receiveLock) and deadlock.
    private int _outgoingResyncPending;
    // Bytes still owed by a previously-rejected over-max frame. ReadMessageAsync drains
    // these from the read channel before reading the next length prefix so that one
    // over-cap message does not permanently desync the framing for every subsequent
    // message (#298). Mirrors the MessageTransit pattern established by #286.
    private uint _pendingDrainRemaining;
    // Set once when the inbound channel reaches real EOF (read returns 0 bytes).
    // ReceiveAllAsync consults this — NOT IsConnected — so transient transport
    // disconnects during auto-reconnect do not prematurely terminate the
    // enumerable (#297). Mirrors the MessageTransit pattern established by #177.
    private volatile bool _receiveEof;
    private volatile bool _disposed;
    private volatile bool _readyFired;
    private readonly object _readyLock = new();

    private static readonly byte[] ResyncRequestHeader = [0x02];

    /// <summary>
    /// Creates a DeltaMessageTransit for dynamic JSON types (JsonObject, JsonNode, JsonArray, JsonDocument).
    /// </summary>
    public DeltaMessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        int maxMessageSize = 16 * 1024 * 1024)
        : this(writeChannel, readChannel, null, maxMessageSize)
    {
    }

    /// <summary>
    /// Creates a DeltaMessageTransit for POCOs with Native AOT support.
    /// </summary>
    public DeltaMessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonTypeInfo<T>? typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        _writeChannel = writeChannel;
        _readChannel = readChannel;
        _typeInfo = typeInfo;
        _maxMessageSize = maxMessageSize;

        if (!IsDynamicJsonType(typeof(T)) && typeInfo is null)
        {
            throw new ArgumentNullException(nameof(typeInfo),
                "JsonTypeInfo required for POCO types. Use source-generated JsonSerializerContext for Native AOT compatibility.");
        }

        SubscribeToChannelEvents();
        // Channel.Ready is single-shot; if all configured channels were already
        // ready before we subscribed, the event we wired up will never fire.
        // Synthesise the call so subscribers attached after construction still
        // observe Ready exactly once (#266). OnChannelReady's _readyFired guard
        // makes the synthesised call race-safe against a concurrent genuine event.
        var writeReady = _writeChannel?.IsReady ?? true;
        var readReady = _readChannel?.IsReady ?? true;
        if (writeReady && readReady)
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
        // Fire Ready only when all configured channels are ready
        var writeReady = _writeChannel?.IsReady ?? true;
        var readReady = _readChannel?.IsReady ?? true;
        if (!writeReady || !readReady) return;
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

    /// <summary>Gets whether the transit is ready (all channels confirmed by remote).</summary>
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

    /// <summary>Gets whether the transit is connected.</summary>
    public bool IsConnected => !_disposed &&
        ((_writeChannel?.IsConnected ?? false) || (_readChannel?.IsConnected ?? false));

    /// <summary>Gets the write channel ID.</summary>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <summary>Gets the read channel ID.</summary>
    public string? ReadChannelId => _readChannel?.ChannelId;

    private EventHandler? _readyHandlers;

    /// <summary>Raised once when all channels are confirmed ready. Never fires again.</summary>
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

    /// <summary>Raised each time the underlying transport connects.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised each time the underlying transport disconnects.</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Wait until the transit is confirmed ready (all channels acknowledged by remote).</summary>
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

    /// <summary>
    /// Sends the current state. On first send, transmits full state.
    /// On subsequent sends, transmits only the delta from the last sent state.
    /// </summary>
    public async ValueTask SendAsync(T state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeChannel is null)
            throw new InvalidOperationException("This transit does not have a write channel configured.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends multiple states as a single combined delta, reducing network overhead.
    /// </summary>
    public async ValueTask SendBatchAsync(IEnumerable<T> states, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeChannel is null)
            throw new InvalidOperationException("This transit does not have a write channel configured.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendBatchCoreAsync(states, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async ValueTask SendCoreAsync(T state, CancellationToken cancellationToken)
    {
        ConsumeResyncRequest();

        var currentState = ToJsonNode(state);

        if (_lastSentState is null)
        {
            await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ops = DeltaDiff.ComputeDelta(_lastSentState, currentState);
            if (ops.Count == 0 || RequiresFullState(ops))
            {
                await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendDeltaAsync(ops, cancellationToken).ConfigureAwait(false);
            }
        }

        _lastSentState = currentState.DeepClone();
    }

    private async ValueTask SendBatchCoreAsync(IEnumerable<T> states, CancellationToken cancellationToken)
    {
        ConsumeResyncRequest();

        // Stage all batched mutations on a local clone so an exception during the
        // accumulated delta flush does not advance _lastSentState past what the peer
        // actually received. _lastSentState is only committed after a successful send.
        var stagedBaseline = _lastSentState?.DeepClone();
        var combinedOps = new List<DeltaOperation>();

        foreach (var state in states)
        {
            var currentState = ToJsonNode(state);

            if (stagedBaseline is null)
            {
                await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
                stagedBaseline = currentState.DeepClone();
                _lastSentState = currentState.DeepClone();
                combinedOps.Clear();
            }
            else
            {
                var ops = DeltaDiff.ComputeDelta(stagedBaseline, currentState);
                if (ops.Count > 0 && !RequiresFullState(ops))
                {
                    DeltaApply.ApplyDelta(stagedBaseline, ops);
                    combinedOps.AddRange(ops);
                }
                else
                {
                    if (combinedOps.Count > 0)
                    {
                        await SendDeltaAsync(combinedOps, cancellationToken).ConfigureAwait(false);
                        _lastSentState = stagedBaseline.DeepClone();
                        combinedOps.Clear();
                    }
                    await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
                    stagedBaseline = currentState.DeepClone();
                    _lastSentState = currentState.DeepClone();
                }
            }
        }

        if (combinedOps.Count > 0)
        {
            await SendDeltaAsync(combinedOps, cancellationToken).ConfigureAwait(false);
            _lastSentState = stagedBaseline!.DeepClone();
        }
    }

    private static bool RequiresFullState(List<DeltaOperation> ops) =>
        ops.Any(op => op.Path.Length == 0 && op.Op is DeltaOp.Set or DeltaOp.SetNull or DeltaOp.Remove);

    /// <summary>
    /// Receives the next state update. Automatically handles full state and delta messages.
    /// Returns default if the channel is closed.
    /// </summary>
    public async ValueTask<T?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readChannel is null)
            throw new InvalidOperationException("This transit does not have a read channel configured.");

        try
        {
            await _receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReceiveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiveLock.Release();
            }
        }
        finally
        {
            // Drain any outgoing resync request flagged by ReceiveCoreAsync AFTER
            // _receiveLock has been released. Acquiring _sendLock while holding
            // _receiveLock would invert the lock order against ResetState (which
            // acquires _sendLock first), risking deadlock. Writing without
            // _sendLock would interleave bytes with concurrent SendAsync calls
            // and corrupt wire framing (#193).
            await DrainOutgoingResyncAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<T?> ReceiveCoreAsync(CancellationToken cancellationToken)
    {
        var (data, messageLength) = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
        if (data is null || messageLength == 0)
            return default;

        try
        {
            var messageType = data[0];
            var payload = data.AsMemory(1, messageLength - 1);

            switch (messageType)
            {
                case 0x00: // Full state
                    var fullState = JsonNode.Parse(payload.Span);
                    _lastReceivedState = fullState?.DeepClone();
                    return FromJsonNode(fullState);

                case 0x01: // Delta
                    if (_lastReceivedState is null)
                    {
                        // Defer the resync write to the ReceiveAsync wrapper so it
                        // happens under _sendLock after _receiveLock is released (#193).
                        Interlocked.Exchange(ref _outgoingResyncPending, 1);
                        return default;
                    }
                    var ops = DeserializeDelta(payload.Span);
                    // Apply atomically: stage on a clone, swap on success. If any op
                    // throws mid-batch (path not found, array index out of range,
                    // incompatible parent type, ...), _lastReceivedState is left
                    // untouched, then cleared and a resync is requested so the peer
                    // re-sends full state. Without this, an earlier op's mutation
                    // would leak into _lastReceivedState and every subsequent delta
                    // would be applied to a corrupt baseline (#223).
                    var staging = _lastReceivedState.DeepClone();
                    try
                    {
                        DeltaApply.ApplyDelta(staging, ops);
                    }
                    catch (Exception ex)
                    {
                        _lastReceivedState = null;
                        // Defer the resync write to the ReceiveAsync wrapper so it
                        // happens under _sendLock after _receiveLock is released (#193).
                        Interlocked.Exchange(ref _outgoingResyncPending, 1);
                        throw new InvalidOperationException(
                            "Delta apply failed; receiver state has been reset and a resync has been requested from the peer.",
                            ex);
                    }
                    _lastReceivedState = staging;
                    return FromJsonNode(_lastReceivedState);

                case 0x02: // Resync request
                    // Defer the _lastSentState reset to the send path so it happens under
                    // _sendLock, avoiding the cross-lock race against ComputeDelta (#300).
                    Interlocked.Exchange(ref _resyncRequested, 1);
                    return default;

                default:
                    throw new InvalidOperationException($"Unknown message type: {messageType}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>
    /// Receives all state updates as an async enumerable.
    /// </summary>
    /// <remarks>
    /// Termination conditions: the supplied <paramref name="cancellationToken"/> is
    /// cancelled, the transit is disposed, or the inbound channel reaches real
    /// end-of-stream. Transient transport disconnects do NOT terminate the
    /// enumerable — when auto-reconnect is configured, iteration resumes after the
    /// mux re-establishes the connection (#297).
    /// </remarks>
    public async IAsyncEnumerable<T> ReceiveAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            T? state;
            try
            {
                state = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            // EOF is signalled exclusively via _receiveEof. A null return is
            // legitimate mid-stream control traffic (resync request received,
            // delta-before-baseline triggering a resync request to the peer) —
            // breaking on null would conflate control frames with real EOF and
            // bail on every resync handshake (#297).
            if (_receiveEof)
                yield break;

            if (state is not null)
                yield return state;
        }
    }

    /// <summary>
    /// Resets the local state, forcing full state transmission on next send.
    /// Also clears the last received state so the next inbound delta will trigger
    /// a resync request to the peer. Use this as a recovery API when the
    /// application detects state corruption or wants to start a fresh sync.
    /// </summary>
    public void ResetState()
    {
        // Acquire both locks so the reset is atomic with respect to in-flight
        // SendCoreAsync / ReceiveCoreAsync calls. Without this, a concurrent send
        // could observe _lastSentState going null mid-ComputeDelta (#300).
        _sendLock.Wait();
        try
        {
            _receiveLock.Wait();
            try
            {
                _lastSentState = null;
                _lastReceivedState = null;
                Interlocked.Exchange(ref _resyncRequested, 0);
            }
            finally
            {
                _receiveLock.Release();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ConsumeResyncRequest()
    {
        if (Interlocked.Exchange(ref _resyncRequested, 0) == 1)
        {
            _lastSentState = null;
        }
    }

    private async ValueTask SendFullAsync(JsonNode state, CancellationToken cancellationToken)
    {
        var json = state.ToJsonString();
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        var message = ArrayPool<byte>.Shared.Rent(1 + byteCount);
        try
        {
            message[0] = 0x00;
            System.Text.Encoding.UTF8.GetBytes(json, message.AsSpan(1));
            await WriteMessageAsync(message.AsMemory(0, 1 + byteCount), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    private async ValueTask SendDeltaAsync(List<DeltaOperation> ops, CancellationToken cancellationToken)
    {
        var json = SerializeDelta(ops);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        var message = ArrayPool<byte>.Shared.Rent(1 + byteCount);
        try
        {
            message[0] = 0x01;
            System.Text.Encoding.UTF8.GetBytes(json, message.AsSpan(1));
            await WriteMessageAsync(message.AsMemory(0, 1 + byteCount), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    private async ValueTask DrainOutgoingResyncAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _outgoingResyncPending, 0) != 1)
            return;
        if (_writeChannel is null)
            return;

        // Acquire _sendLock so the 5-byte resync frame is serialized against any
        // concurrent SendAsync / SendBatchAsync write on the same channel. This
        // method is invoked from ReceiveAsync's outer finally and MUST NOT throw:
        // throwing here would swallow an InvalidOperationException raised by
        // ReceiveCoreAsync (e.g. on ApplyDelta failure). On any failure we
        // re-flag _outgoingResyncPending so the next ReceiveAsync drains it again.
        bool acquired = false;
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await WriteMessageAsync(ResyncRequestHeader, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _outgoingResyncPending, 1);
        }
        finally
        {
            if (acquired) _sendLock.Release();
        }
    }

    private async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_writeChannel is null) return;

        var totalLength = 4 + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
            data.CopyTo(buffer.AsMemory(4));
            await _writeChannel.WriteAsync(buffer.AsMemory(0, totalLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<(byte[]? buffer, int length)> ReadMessageAsync(CancellationToken cancellationToken)
    {
        if (_readChannel is null) return (null, 0);

        // Drain any bytes owed by a previously-rejected over-max frame so the next
        // length prefix lines up with a real frame boundary on the wire (#298).
        if (_pendingDrainRemaining > 0)
        {
            await DrainPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        var lengthPrefix = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var prefixRead = 0;
            while (prefixRead < 4)
            {
                var bytesRead = await _readChannel.ReadAsync(lengthPrefix.AsMemory(prefixRead, 4 - prefixRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    _receiveEof = true;
                    return (null, 0);
                }
                prefixRead += bytesRead;
            }

            var messageLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix.AsSpan(0, 4));

            if (messageLength > (uint)_maxMessageSize)
            {
                // Schedule the over-cap payload for drain on the next ReadMessageAsync
                // so framing resumes on a clean boundary instead of parsing payload
                // bytes as the next length prefix.
                _pendingDrainRemaining = messageLength;
                throw new InvalidOperationException(
                    $"Received message size ({messageLength} bytes) exceeds maximum allowed ({_maxMessageSize} bytes); the oversized payload will be discarded and the next ReceiveAsync call will return the following message.");
            }

            if (messageLength == 0)
            {
                // Zero-length frame has no payload to drain and no message-type byte
                // either — framing is unrecoverable. The caller decides whether to
                // continue using the transit; subsequent reads will likely throw on
                // the next misaligned prefix.
                throw new InvalidOperationException("Received a message with zero-length payload.");
            }

            var length = (int)messageLength;
            var data = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var totalRead = 0;
                while (totalRead < length)
                {
                    var read = await _readChannel.ReadAsync(data.AsMemory(totalRead, length - totalRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        ArrayPool<byte>.Shared.Return(data);
                        _receiveEof = true;
                        return (null, 0);
                    }
                    totalRead += read;
                }

                return (data, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(data);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthPrefix);
        }
    }

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
                    // Channel closed before the over-max payload finished draining.
                    // Framing is unrecoverable; surface as end-of-stream.
                    _pendingDrainRemaining = 0;
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

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only adding primitive JsonValue and JsonNode which are AOT-safe")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only adding primitive JsonValue and JsonNode which are AOT-safe")]
    internal static string SerializeDelta(List<DeltaOperation> ops)
    {
        var array = new JsonArray();
        foreach (var op in ops)
        {
            var opArray = new JsonArray();
            opArray.Add(JsonValue.Create((int)op.Op));
            opArray.Add(PathToJsonArray(op.Path));

            if (op.Op is DeltaOp.Set or DeltaOp.ArrayInsert or DeltaOp.ArrayReplace)
            {
                opArray.Add(op.Value?.DeepClone());
            }

            if (op.Index.HasValue)
            {
                opArray.Add(JsonValue.Create(op.Index.Value));
            }

            array.Add(opArray);
        }
        return array.ToJsonString();
    }

    internal static List<DeltaOperation> DeserializeDelta(ReadOnlySpan<byte> json)
    {
        var ops = new List<DeltaOperation>();
        var array = JsonNode.Parse(json);
        if (array is not JsonArray opsArray)
            throw new JsonException("Delta payload must be a JSON array.");

        foreach (var item in opsArray)
        {
            if (item is not JsonArray opArray || opArray.Count < 2)
                throw new JsonException("Each delta operation must be an array with at least 2 elements.");

            if (opArray[0] is not JsonValue opVal || !opVal.TryGetValue<int>(out var rawOpCode))
                throw new JsonException("Delta operation code must be an integer.");
            if (!Enum.IsDefined((DeltaOp)rawOpCode))
                throw new JsonException($"Unknown delta operation code: {rawOpCode}");

            var opCode = (DeltaOp)rawOpCode;
            if (opArray[1] is not JsonArray pathArray)
                throw new JsonException("Delta operation path must be a JSON array.");
            var path = JsonArrayToPath(pathArray);

            JsonNode? value = null;
            int? index = null;

            switch (opCode)
            {
                case DeltaOp.Set:
                case DeltaOp.ArrayReplace:
                    value = opArray.Count > 2 ? opArray[2]?.DeepClone() : null;
                    break;

                case DeltaOp.ArrayInsert:
                    value = opArray.Count > 2 ? opArray[2]?.DeepClone() : null;
                    index = opArray.Count > 3 ? GetOptionalInt(opArray[3], "array insert index") : null;
                    break;

                case DeltaOp.ArrayRemove:
                    index = opArray.Count > 2 ? GetOptionalInt(opArray[2], "array remove index") : null;
                    break;
            }

            ops.Add(new DeltaOperation(opCode, path, value, index));
        }

        return ops;
    }

    private static int? GetOptionalInt(JsonNode? node, string fieldName)
    {
        if (node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var intVal))
            return intVal;
        throw new JsonException($"Delta {fieldName} must be an integer.");
    }

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only adding primitive JsonValue (int, string) which are AOT-safe")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only adding primitive JsonValue (int, string) which are AOT-safe")]
    private static JsonArray PathToJsonArray(object[] path)
    {
        var array = new JsonArray();
        foreach (var segment in path)
        {
            if (segment is string s)
                array.Add(JsonValue.Create(s));
            else if (segment is int i)
                array.Add(JsonValue.Create(i));
        }
        return array;
    }

    private static object[] JsonArrayToPath(JsonArray array)
    {
        var path = new object[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            var node = array[i];
            if (node is JsonValue val)
            {
                if (val.TryGetValue<int>(out var intVal))
                    path[i] = intVal;
                else if (val.TryGetValue<string>(out var strVal))
                    path[i] = strVal!;
            }
        }
        return path;
    }

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _typeInfo is null, meaning a dynamic JSON type is used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _typeInfo is null, meaning a dynamic JSON type is used")]
    private JsonNode ToJsonNode(T value)
    {
        return value switch
        {
            JsonNode node => node.DeepClone(),
            JsonDocument doc => JsonNode.Parse(doc.RootElement.GetRawText())!,
            JsonElement elem => JsonNode.Parse(elem.GetRawText())!,
            _ when _typeInfo is not null => JsonSerializer.SerializeToNode(value, _typeInfo)!,
            _ => throw new InvalidOperationException("Cannot serialize POCO without JsonTypeInfo")
        };
    }

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _typeInfo is null, meaning a dynamic JSON type is used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _typeInfo is null, meaning a dynamic JSON type is used")]
    private T? FromJsonNode(JsonNode? node)
    {
        if (node is null) return default;

        var type = typeof(T);

        if (type == typeof(JsonNode))
            return (T)(object)node.DeepClone();

        if (type == typeof(JsonObject))
            // Issue #241: AsObject() is a downcast, not a clone — without DeepClone
            // the caller would receive a live reference to _lastReceivedState, so
            // caller mutations would silently corrupt internal state and a concurrent
            // delta-apply would throw "Collection was modified" mid-enumeration.
            return (T)(object)node.DeepClone().AsObject();

        if (type == typeof(JsonArray))
            // Issue #241: same aliasing hazard as JsonObject above — DeepClone first.
            return (T)(object)node.DeepClone().AsArray();

        if (type == typeof(JsonDocument))
            return (T)(object)JsonDocument.Parse(node.ToJsonString());

        if (type == typeof(JsonElement))
            return (T)(object)JsonDocument.Parse(node.ToJsonString()).RootElement;

        if (_typeInfo is not null)
            return node.Deserialize(_typeInfo);

        throw new InvalidOperationException("Cannot deserialize to POCO without JsonTypeInfo");
    }

    private static bool IsDynamicJsonType(Type t) =>
        t == typeof(JsonNode) ||
        t == typeof(JsonObject) ||
        t == typeof(JsonArray) ||
        t == typeof(JsonDocument) ||
        t == typeof(JsonElement);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        UnsubscribeFromChannelEvents();

        // Aggregate per-step failures so a throw from one channel's dispose does not
        // skip the other channel and leak its slab (#292, mirroring StreamPair PR #224
        // / DuplexStreamTransit #305). Channels disposed first so semaphores remain
        // valid until any in-flight send/receive observes ObjectDisposedException.
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
        try { _sendLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        try { _receiveLock.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }

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
