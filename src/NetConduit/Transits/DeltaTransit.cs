using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Enums;
using NetConduit.Internal;
using NetConduit.Models;

namespace NetConduit.Transits;

/// <summary>
/// A transit that sends and receives state changes as deltas, minimizing bandwidth.
/// Only changed properties are transmitted after the first full state sync.
/// Thread-safe and optimized for high-frequency payloads.
/// </summary>
/// <typeparam name="T">The type of state to synchronize.</typeparam>
public sealed class DeltaTransit<T> : IAsyncDisposable
{
    private readonly WriteChannel? _writeChannel;
    private readonly ReadChannel? _readChannel;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly int _maxMessageSize;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);

    private JsonNode? _lastSentState;
    private JsonNode? _lastReceivedState;
    private volatile bool _disposed;

    private static readonly byte[] ResyncRequestHeader = [0x02];

    /// <summary>
    /// Creates a DeltaTransit for dynamic JSON types (JsonObject, JsonNode, JsonArray, JsonDocument).
    /// </summary>
    public DeltaTransit(
        WriteChannel? writeChannel,
        ReadChannel? readChannel,
        int maxMessageSize = 16 * 1024 * 1024)
        : this(writeChannel, readChannel, null, maxMessageSize)
    {
    }

    /// <summary>
    /// Creates a DeltaTransit for POCOs with Native AOT support.
    /// </summary>
    public DeltaTransit(
        WriteChannel? writeChannel,
        ReadChannel? readChannel,
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
    }

    /// <summary>Gets whether the transit is connected.</summary>
    public bool IsConnected => !_disposed &&
        (_writeChannel?.State == ChannelState.Open || _readChannel?.State == ChannelState.Open);

    /// <summary>Gets the write channel ID.</summary>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <summary>Gets the read channel ID.</summary>
    public string? ReadChannelId => _readChannel?.ChannelId;

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
        var currentState = ToJsonNode(state);

        if (_lastSentState is null)
        {
            await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ops = DeltaDiff.ComputeDelta(_lastSentState, currentState);
            if (ops.Count == 0)
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
        JsonNode? finalState = null;
        var combinedOps = new List<DeltaOperation>();

        foreach (var state in states)
        {
            var currentState = ToJsonNode(state);
            finalState = currentState;

            if (_lastSentState is null)
            {
                await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
                _lastSentState = currentState.DeepClone();
                combinedOps.Clear();
            }
            else
            {
                var ops = DeltaDiff.ComputeDelta(_lastSentState, currentState);
                if (ops.Count > 0)
                {
                    DeltaApply.ApplyDelta(_lastSentState, ops);
                    combinedOps.AddRange(ops);
                }
                else
                {
                    if (combinedOps.Count > 0)
                    {
                        await SendDeltaAsync(combinedOps, cancellationToken).ConfigureAwait(false);
                        combinedOps.Clear();
                    }
                    await SendFullAsync(currentState, cancellationToken).ConfigureAwait(false);
                    _lastSentState = currentState.DeepClone();
                }
            }
        }

        if (combinedOps.Count > 0)
        {
            await SendDeltaAsync(combinedOps, cancellationToken).ConfigureAwait(false);
        }

        if (finalState is not null)
        {
            _lastSentState = finalState.DeepClone();
        }
    }

    /// <summary>
    /// Receives the next state update. Automatically handles full state and delta messages.
    /// Returns default if the channel is closed.
    /// </summary>
    public async ValueTask<T?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readChannel is null)
            throw new InvalidOperationException("This transit does not have a read channel configured.");

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
                        await RequestResyncAsync(cancellationToken).ConfigureAwait(false);
                        return default;
                    }
                    var ops = DeserializeDelta(payload.Span);
                    DeltaApply.ApplyDelta(_lastReceivedState, ops);
                    return FromJsonNode(_lastReceivedState);

                case 0x02: // Resync request
                    _lastSentState = null;
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
    public async IAsyncEnumerable<T> ReceiveAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            var state = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                yield return state;
            }
            else if (!IsConnected)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Resets the local state, forcing full state transmission on next send.
    /// </summary>
    public void ResetState()
    {
        _lastSentState = null;
        _lastReceivedState = null;
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

    private async ValueTask RequestResyncAsync(CancellationToken cancellationToken)
    {
        if (_writeChannel is not null)
        {
            await WriteMessageAsync(ResyncRequestHeader, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_writeChannel is null) return;

        var totalLength = 4 + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
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

        var lengthPrefix = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var prefixRead = 0;
            while (prefixRead < 4)
            {
                var bytesRead = await _readChannel.ReadAsync(lengthPrefix.AsMemory(prefixRead, 4 - prefixRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) return (null, 0);
                prefixRead += bytesRead;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix.AsSpan(0, 4));
            if (length <= 0 || length > _maxMessageSize)
                throw new InvalidOperationException($"Invalid message length: {length}");

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
            return (T)(object)node.AsObject();

        if (type == typeof(JsonArray))
            return (T)(object)node.AsArray();

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

        _sendLock.Dispose();
        _receiveLock.Dispose();

        if (_writeChannel is not null)
            await _writeChannel.DisposeAsync().ConfigureAwait(false);

        if (_readChannel is not null)
            await _readChannel.DisposeAsync().ConfigureAwait(false);
    }
}
