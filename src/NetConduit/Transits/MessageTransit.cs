using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetConduit.Transits;

/// <summary>
/// A transit that sends and receives discrete JSON-serialized messages over a channel pair.
/// Uses length-prefixed framing (4-byte big-endian length prefix) for message boundaries.
/// Fully AOT-compatible when using JsonTypeInfo overloads.
/// </summary>
/// <typeparam name="TSend">The type of messages to send.</typeparam>
/// <typeparam name="TReceive">The type of messages to receive.</typeparam>
public sealed class MessageTransit<TSend, TReceive> : IMessageTransit<TSend, TReceive>
{
    private readonly WriteChannel? _writeChannel;
    private readonly ReadChannel? _readChannel;
    private readonly JsonTypeInfo<TSend>? _sendTypeInfo;
    private readonly JsonTypeInfo<TReceive>? _receiveTypeInfo;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly int _maxMessageSize;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new MessageTransit with both send and receive capabilities using AOT-safe JsonTypeInfo.
    /// </summary>
    /// <param name="writeChannel">The channel for sending messages (can be null for receive-only).</param>
    /// <param name="readChannel">The channel for receiving messages (can be null for send-only).</param>
    /// <param name="sendTypeInfo">The JSON type info for serializing send messages.</param>
    /// <param name="receiveTypeInfo">The JSON type info for deserializing received messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    public MessageTransit(
        WriteChannel? writeChannel,
        ReadChannel? readChannel,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        _writeChannel = writeChannel;
        _readChannel = readChannel;
        _sendTypeInfo = sendTypeInfo;
        _receiveTypeInfo = receiveTypeInfo;
        _maxMessageSize = maxMessageSize;
    }

    /// <summary>
    /// Creates a new MessageTransit with both send and receive capabilities using JsonSerializerOptions.
    /// Note: This overload uses reflection and is not AOT-compatible.
    /// </summary>
    /// <param name="writeChannel">The channel for sending messages (can be null for receive-only).</param>
    /// <param name="readChannel">The channel for receiving messages (can be null for send-only).</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public MessageTransit(
        WriteChannel? writeChannel,
        ReadChannel? readChannel,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        _writeChannel = writeChannel;
        _readChannel = readChannel;
        _jsonOptions = jsonOptions;
        _maxMessageSize = maxMessageSize;
    }

    /// <inheritdoc/>
    public bool IsConnected => !_disposed &&
        (_writeChannel?.State == ChannelState.Open || _readChannel?.State == ChannelState.Open);

    /// <inheritdoc/>
    public string? WriteChannelId => _writeChannel?.ChannelId;

    /// <inheritdoc/>
    public string? ReadChannelId => _readChannel?.ChannelId;

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _sendTypeInfo is null, meaning the non-AOT constructor was used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _sendTypeInfo is null, meaning the non-AOT constructor was used")]
    public async ValueTask SendAsync(TSend message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writeChannel is null)
            throw new InvalidOperationException("This transit does not have a write channel configured.");

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

        // Combine length prefix + payload into single write for atomicity
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

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Only called when _receiveTypeInfo is null, meaning the non-AOT constructor was used")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Only called when _receiveTypeInfo is null, meaning the non-AOT constructor was used")]
    public async ValueTask<TReceive?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_readChannel is null)
            throw new InvalidOperationException("This transit does not have a read channel configured.");

        // Read length prefix (4 bytes, big-endian)
        var lengthBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var bytesRead = await ReadExactAsync(_readChannel, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return default; // Channel closed

            var messageLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);

            if (messageLength > _maxMessageSize)
                throw new InvalidOperationException($"Received message size ({messageLength} bytes) exceeds maximum allowed ({_maxMessageSize} bytes).");

            if (messageLength == 0)
            {
                // Empty message - return default for reference types, or handle appropriately
                return default;
            }

            // Read message payload
            var messageBuffer = ArrayPool<byte>.Shared.Rent((int)messageLength);
            try
            {
                bytesRead = await ReadExactAsync(_readChannel, messageBuffer.AsMemory(0, (int)messageLength), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    return default; // Channel closed during read

                if (_receiveTypeInfo is not null)
                {
                    return JsonSerializer.Deserialize(messageBuffer.AsSpan(0, (int)messageLength), _receiveTypeInfo);
                }
                else
                {
                    return JsonSerializer.Deserialize<TReceive>(messageBuffer.AsSpan(0, (int)messageLength), _jsonOptions);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(messageBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBuffer);
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

            if (message is null)
                yield break; // Channel closed

            yield return message;
        }
    }

    private static async ValueTask<int> ReadExactAsync(ReadChannel channel, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await channel.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return totalRead == 0 ? 0 : throw new EndOfStreamException("Unexpected end of stream while reading message.");

            totalRead += bytesRead;
        }
        return totalRead;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_writeChannel is not null)
            await _writeChannel.DisposeAsync().ConfigureAwait(false);

        if (_readChannel is not null)
            await _readChannel.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _writeChannel?.Dispose();
        _readChannel?.Dispose();
    }
}

/// <summary>
/// Extension methods for creating MessageTransit instances from multiplexer channels.
/// </summary>
public static class MessageTransitExtensions
{
    /// <summary>
    /// Creates a bidirectional message transit from a write channel and read channel pair.
    /// </summary>
    public static MessageTransit<TSend, TReceive> CreateMessageTransit<TSend, TReceive>(
        this WriteChannel writeChannel,
        ReadChannel readChannel,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Creates a send-only message transit from a write channel.
    /// </summary>
    public static MessageTransit<T, T> CreateSendOnlyTransit<T>(
        this WriteChannel writeChannel,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        return new MessageTransit<T, T>(writeChannel, null, typeInfo, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Creates a receive-only message transit from a read channel.
    /// </summary>
    public static MessageTransit<T, T> CreateReceiveOnlyTransit<T>(
        this ReadChannel readChannel,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        return new MessageTransit<T, T>(null, readChannel, typeInfo, typeInfo, maxMessageSize);
    }
}
