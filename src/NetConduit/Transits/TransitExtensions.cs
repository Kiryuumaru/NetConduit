using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetConduit.Transits;

/// <summary>
/// Extension methods for creating transits from multiplexers.
/// </summary>
public static class TransitExtensions
{
    /// <summary>
    /// Suffix appended to channel ID for outbound (write) channels in duplex transits.
    /// </summary>
    public const string OutboundSuffix = ">>";

    /// <summary>
    /// Suffix appended to channel ID for inbound (read) channels in duplex transits.
    /// </summary>
    public const string InboundSuffix = "<<";

    #region StreamMultiplexer Extensions

    /// <summary>
    /// Opens a channel and wraps it as a write-only Stream.
    /// </summary>
    /// <param name="mux">The multiplexer to open the channel on.</param>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write-only StreamTransit.</returns>
    public static async Task<StreamTransit> OpenStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cancellationToken);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Opens a channel and wraps it as a write-only Stream with custom options.
    /// </summary>
    /// <param name="mux">The multiplexer to open the channel on.</param>
    /// <param name="options">The channel options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write-only StreamTransit.</returns>
    public static async Task<StreamTransit> OpenStreamAsync(
        this IStreamMultiplexer mux,
        ChannelOptions options,
        CancellationToken cancellationToken = default)
    {
        var channel = await mux.OpenChannelAsync(options, cancellationToken);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Accepts a channel and wraps it as a read-only Stream.
    /// </summary>
    /// <param name="mux">The multiplexer to accept the channel from.</param>
    /// <param name="channelId">The channel identifier to accept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only StreamTransit.</returns>
    public static async Task<StreamTransit> AcceptStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Opens a write channel and accepts a read channel, then wraps them as a bidirectional Stream.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// </summary>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will append "&gt;&gt;" for write and "&lt;&lt;" for read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bidirectional DuplexStreamTransit.</returns>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + OutboundSuffix }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Accepts a read channel and opens a write channel, then wraps them as a bidirectional Stream.
    /// Uses "{channelId}&lt;&lt;" for reading (accepts the opener's outbound) and "{channelId}&gt;&gt;" for writing (opens to the opener's inbound).
    /// This is the counterpart to <see cref="OpenDuplexStreamAsync(IStreamMultiplexer, string, CancellationToken)"/>.
    /// </summary>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will accept "&gt;&gt;" for read and open "&lt;&lt;" for write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bidirectional DuplexStreamTransit.</returns>
    public static async Task<DuplexStreamTransit> AcceptDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + InboundSuffix }, cancellationToken);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Opens a write channel and accepts a read channel, then wraps them as a bidirectional Stream.
    /// </summary>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="writeChannelId">The channel ID for writing (will be opened).</param>
    /// <param name="readChannelId">The channel ID for reading (will be accepted).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bidirectional DuplexStreamTransit.</returns>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = writeChannelId }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will append "&gt;&gt;" for write and "&lt;&lt;" for read.</param>
    /// <param name="sendTypeInfo">The JSON type info for serializing send messages.</param>
    /// <param name="receiveTypeInfo">The JSON type info for deserializing received messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + OutboundSuffix }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit by accepting a read channel and opening a write channel.
    /// Uses "{channelId}&gt;&gt;" for reading (accepts the opener's outbound) and "{channelId}&lt;&lt;" for writing (opens to the opener's inbound).
    /// This is the counterpart to <see cref="OpenMessageTransitAsync{TSend, TReceive}(IStreamMultiplexer, string, JsonTypeInfo{TSend}, JsonTypeInfo{TReceive}, int, CancellationToken)"/>.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will accept "&gt;&gt;" for read and open "&lt;&lt;" for write.</param>
    /// <param name="sendTypeInfo">The JSON type info for serializing send messages.</param>
    /// <param name="receiveTypeInfo">The JSON type info for deserializing received messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
    public static async Task<MessageTransit<TSend, TReceive>> AcceptMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId + OutboundSuffix, cancellationToken);
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + InboundSuffix }, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="writeChannelId">The channel ID for sending (will be opened).</param>
    /// <param name="readChannelId">The channel ID for receiving (will be accepted).</param>
    /// <param name="sendTypeInfo">The JSON type info for serializing send messages.</param>
    /// <param name="receiveTypeInfo">The JSON type info for deserializing received messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = writeChannelId }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a send-only message transit using AOT-safe JsonTypeInfo.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The channel ID for sending.</param>
    /// <param name="sendTypeInfo">The JSON type info for serializing send messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A send-only MessageTransit.</returns>
    public static async Task<MessageTransit<TSend, object>> OpenSendOnlyMessageTransitAsync<TSend>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cancellationToken);
        return new MessageTransit<TSend, object>(writeChannel, null, sendTypeInfo, null!, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using AOT-safe JsonTypeInfo.
    /// </summary>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The channel ID to accept for receiving.</param>
    /// <param name="receiveTypeInfo">The JSON type info for deserializing received messages.</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A receive-only MessageTransit.</returns>
    public static async Task<MessageTransit<object, TReceive>> AcceptReceiveOnlyMessageTransitAsync<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var readChannel = await mux.AcceptChannelAsync(channelId, cancellationToken);
        return new MessageTransit<object, TReceive>(null, readChannel, null!, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses reflection for serialization. Note: Not AOT-compatible.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will append "&gt;&gt;" for write and "&lt;&lt;" for read.</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + OutboundSuffix }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(channelId + InboundSuffix, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit by accepting a read channel and opening a write channel.
    /// Uses "{channelId}&gt;&gt;" for reading (accepts the opener's outbound) and "{channelId}&lt;&lt;" for writing (opens to the opener's inbound).
    /// This is the counterpart to <see cref="OpenMessageTransitAsync{TSend, TReceive}(IStreamMultiplexer, string, JsonSerializerOptions?, int, CancellationToken)"/>.
    /// Uses reflection for serialization. Note: Not AOT-compatible.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The base channel ID. Will accept "&gt;&gt;" for read and open "&lt;&lt;" for write.</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
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
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId + InboundSuffix }, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses reflection for serialization. Note: Not AOT-compatible.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="writeChannelId">The channel ID for sending (will be opened).</param>
    /// <param name="readChannelId">The channel ID for receiving (will be accepted).</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A MessageTransit for bidirectional messaging.</returns>
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
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = writeChannelId }, cancellationToken);
        var readChannel = await mux.AcceptChannelAsync(readChannelId, cancellationToken);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a send-only message transit using reflection. Note: Not AOT-compatible.
    /// </summary>
    /// <typeparam name="TSend">The type of messages to send.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The channel ID for sending.</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A send-only MessageTransit.</returns>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static async Task<MessageTransit<TSend, object>> OpenSendOnlyMessageTransitAsync<TSend>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var writeChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cancellationToken);
        return new MessageTransit<TSend, object>(writeChannel, null, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using reflection. Note: Not AOT-compatible.
    /// </summary>
    /// <typeparam name="TReceive">The type of messages to receive.</typeparam>
    /// <param name="mux">The multiplexer.</param>
    /// <param name="channelId">The channel ID to accept for receiving.</param>
    /// <param name="jsonOptions">JSON serializer options (optional).</param>
    /// <param name="maxMessageSize">Maximum message size in bytes (default: 16MB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A receive-only MessageTransit.</returns>
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
}
