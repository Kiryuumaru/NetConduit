using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Enums;

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
    private readonly IWriteChannel? _writeChannel;
    private readonly IReadChannel? _readChannel;
    private readonly JsonTypeInfo<TSend>? _sendTypeInfo;
    private readonly JsonTypeInfo<TReceive>? _receiveTypeInfo;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly int _maxMessageSize;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private volatile bool _disposed;

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
            var lengthBuffer = ArrayPool<byte>.Shared.Rent(4);
            try
            {
                var bytesRead = await ReadExactAsync(_readChannel, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    return default;

                var messageLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);

                if (messageLength > (uint)_maxMessageSize)
                    throw new InvalidOperationException($"Received message size ({messageLength} bytes) exceeds maximum allowed ({_maxMessageSize} bytes).");

                if (messageLength == 0)
                {
                    throw new InvalidOperationException("Received a message with zero-length payload.");
                }

                var messageBuffer = ArrayPool<byte>.Shared.Rent((int)messageLength);
                try
                {
                    bytesRead = await ReadExactAsync(_readChannel, messageBuffer.AsMemory(0, (int)messageLength), cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                        return default;

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
        finally
        {
            _receiveLock.Release();
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
                yield break;

            yield return message;
        }
    }

    private static async ValueTask<int> ReadExactAsync(IReadChannel channel, Memory<byte> buffer, CancellationToken cancellationToken)
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

        _sendLock.Dispose();
        _receiveLock.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _writeChannel?.Dispose();
        _readChannel?.Dispose();
        _sendLock.Dispose();
        _receiveLock.Dispose();
    }
}
