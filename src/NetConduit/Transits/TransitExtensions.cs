using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetConduit.Transits;

/// <summary>
/// Extension methods for creating transits from multiplexers.
/// <para>
/// Duplex transits use a naming convention where two simplex channels form a bidirectional pair.
/// Given a base <c>channelId</c>, the outbound (write) channel is named <c>"{channelId}&gt;&gt;"</c>
/// and the inbound (read) channel is named <c>"{channelId}&lt;&lt;"</c>.
/// The counterpart (<see cref="AcceptDuplexStreamAsync(IStreamMultiplexer, string, CancellationToken)"/>)
/// reverses the roles: it reads from <c>"{channelId}&gt;&gt;"</c> and writes to <c>"{channelId}&lt;&lt;"</c>.
/// </para>
/// <para>
/// The base <c>channelId</c> must not itself contain the suffix sequences <c>"&gt;&gt;"</c> or <c>"&lt;&lt;"</c>,
/// as this would produce ambiguous composite channel names. Callers are responsible for choosing
/// base channel IDs that do not contain these reserved sequences.
/// </para>
/// </summary>
public static class TransitExtensions
{
    /// <summary>
    /// Suffix appended to channel ID for outbound (write) channels in duplex transits.
    /// Base channel IDs must not contain this sequence to avoid naming ambiguity.
    /// </summary>
    public const string OutboundSuffix = ">>";

    /// <summary>
    /// Suffix appended to channel ID for inbound (read) channels in duplex transits.
    /// Base channel IDs must not contain this sequence to avoid naming ambiguity.
    /// </summary>
    public const string InboundSuffix = "<<";

    #region Stream Extensions

    /// <summary>
    /// Opens a channel and wraps it as a write-only Stream.
    /// </summary>
    public static StreamTransit OpenStream(
        this IStreamMultiplexer mux,
        string channelId)
    {
        var channel = mux.OpenChannel(channelId);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Opens a channel with custom options and wraps it as a write-only Stream.
    /// </summary>
    public static StreamTransit OpenStream(
        this IStreamMultiplexer mux,
        ChannelOptions options)
    {
        var channel = mux.OpenChannel(options);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Accepts a channel and wraps it as a read-only Stream.
    /// </summary>
    public static async Task<StreamTransit> AcceptStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new StreamTransit(channel);
    }

    #endregion

    #region Duplex Stream Extensions

    /// <summary>
    /// Opens a write channel and accepts a read channel, then wraps them as a bidirectional Stream.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// </summary>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Accepts a read channel and opens a write channel, then wraps them as a bidirectional Stream.
    /// This is the counterpart to <see cref="OpenDuplexStreamAsync(IStreamMultiplexer, string, CancellationToken)"/>.
    /// </summary>
    public static async Task<DuplexStreamTransit> AcceptDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Opens a write channel and accepts a read channel with explicit channel IDs,
    /// then wraps them as a bidirectional Stream.
    /// </summary>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(writeChannelId);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    #endregion

    #region Message Transit Extensions (AOT-safe)

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to <see cref="OpenMessageTransitAsync{TSend, TReceive}(IStreamMultiplexer, string, JsonTypeInfo{TSend}, JsonTypeInfo{TReceive}, int, CancellationToken)"/>.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<MessageTransit<TSend, TReceive>> AcceptMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit for bidirectional messaging with the same type for send and receive.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static Task<MessageTransit<T, T>> OpenMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => mux.OpenMessageTransitAsync(channelId, typeInfo, typeInfo, maxMessageSize, cancellationToken);

    /// <summary>
    /// Accepts a message transit for bidirectional messaging with the same type for send and receive.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static Task<MessageTransit<T, T>> AcceptMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => mux.AcceptMessageTransitAsync(channelId, typeInfo, typeInfo, maxMessageSize, cancellationToken);

    /// <summary>
    /// Opens a message transit with explicit write and read channel IDs.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(writeChannelId);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a send-only message transit using AOT-safe JsonTypeInfo.
    /// </summary>
    public static MessageTransit<TSend, object> OpenSendOnlyMessageTransit<TSend>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var writeChannel = mux.OpenChannel(channelId);
        return new MessageTransit<TSend, object>(writeChannel, null, sendTypeInfo, null, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using AOT-safe JsonTypeInfo.
    /// </summary>
    public static async Task<MessageTransit<object, TReceive>> AcceptReceiveOnlyMessageTransitAsync<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new MessageTransit<object, TReceive>(null, readChannel, null, receiveTypeInfo, maxMessageSize);
    }

    #endregion

    #region Message Transit Extensions (Reflection)

    /// <summary>
    /// Opens a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// This is the counterpart to the reflection-based OpenMessageTransitAsync.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<TSend, TReceive>> AcceptMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit with the same type for send and receive using reflection. Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static Task<MessageTransit<T, T>> OpenMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => mux.OpenMessageTransitAsync<T, T>(channelId, jsonOptions, maxMessageSize, cancellationToken);

    /// <summary>
    /// Accepts a message transit with the same type for send and receive using reflection. Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static Task<MessageTransit<T, T>> AcceptMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => mux.AcceptMessageTransitAsync<T, T>(channelId, jsonOptions, maxMessageSize, cancellationToken);

    /// <summary>
    /// Opens a message transit with explicit channel IDs using reflection. Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        JsonSerializerOptions? jsonOptions,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(writeChannelId);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a send-only message transit using reflection. Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<TSend, object> OpenSendOnlyMessageTransit<TSend>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var writeChannel = mux.OpenChannel(channelId);
        return new MessageTransit<TSend, object>(writeChannel, null, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using reflection. Not AOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<object, TReceive>> AcceptReceiveOnlyMessageTransitAsync<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new MessageTransit<object, TReceive>(null, readChannel, jsonOptions, maxMessageSize);
    }

    #endregion

    #region Delta Transit Extensions

    /// <summary>
    /// Opens a delta transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<DeltaTransit<T>> OpenDeltaTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new DeltaTransit<T>(writeChannel, readChannel, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a delta transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to <see cref="OpenDeltaTransitAsync{T}(IStreamMultiplexer, string, JsonTypeInfo{T}, int, CancellationToken)"/>.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<DeltaTransit<T>> AcceptDeltaTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        return new DeltaTransit<T>(writeChannel, readChannel, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a send-only delta transit for pushing state updates.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static DeltaTransit<T> OpenSendOnlyDeltaTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var writeChannel = mux.OpenChannel(channelId);
        return new DeltaTransit<T>(writeChannel, null, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only delta transit for receiving state updates.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    public static async Task<DeltaTransit<T>> AcceptReceiveOnlyDeltaTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new DeltaTransit<T>(null, readChannel, typeInfo, maxMessageSize);
    }

    #endregion
}
