using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.Message;

/// <summary>
/// Extension methods on <see cref="IStreamMultiplexer"/> for creating <see cref="MessageTransit{TSend, TReceive}"/> instances.
/// <para>
/// Duplex transits use a naming convention where two simplex channels form a bidirectional pair.
/// Given a base <c>channelId</c>, the outbound (write) channel is named <c>"{channelId}&gt;&gt;"</c>
/// and the inbound (read) channel is named <c>"{channelId}&lt;&lt;"</c>. The counterpart
/// reverses the roles: it reads from <c>"{channelId}&gt;&gt;"</c> and writes to <c>"{channelId}&lt;&lt;"</c>.
/// </para>
/// <para>
/// The base <c>channelId</c> must not itself contain the suffix sequences <c>"&gt;&gt;"</c> or <c>"&lt;&lt;"</c>,
/// as this would produce ambiguous composite channel names.
/// </para>
/// </summary>
public static class MessageTransitExtensions
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

    #region AOT-safe (JsonTypeInfo)

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state. Use WaitForReadyAsync to wait for readiness.
    /// </summary>
    public static MessageTransit<TSend, TReceive> OpenMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var (writeChannel, readChannel) = RegisterPair(mux, channelId + OutboundSuffix, channelId + InboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.OpenMessageTransit(channelId, sendTypeInfo, receiveTypeInfo, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Accepts a message transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to OpenMessageTransit.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state. Use WaitForReadyAsync to wait for readiness.
    /// </summary>
    public static MessageTransit<TSend, TReceive> AcceptMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var (writeChannel, readChannel) = RegisterPair(mux, channelId + InboundSuffix, channelId + OutboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to OpenMessageTransitAsync.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<MessageTransit<TSend, TReceive>> AcceptMessageTransitAsync<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptMessageTransit(channelId, sendTypeInfo, receiveTypeInfo, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Opens a message transit for bidirectional messaging with the same type for send and receive.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state.
    /// </summary>
    public static MessageTransit<T, T> OpenMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
        => mux.OpenMessageTransit(channelId, typeInfo, typeInfo, maxMessageSize);

    /// <summary>
    /// Opens a message transit for bidirectional messaging with the same type for send and receive.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
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
    /// Returns immediately in pending state.
    /// </summary>
    public static MessageTransit<T, T> AcceptMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
        => mux.AcceptMessageTransit(channelId, typeInfo, typeInfo, maxMessageSize);

    /// <summary>
    /// Accepts a message transit for bidirectional messaging with the same type for send and receive.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
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
    /// Returns immediately in pending state.
    /// </summary>
    public static MessageTransit<TSend, TReceive> OpenMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var (writeChannel, readChannel) = RegisterPair(mux, writeChannelId, readChannelId);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, sendTypeInfo, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit with explicit write and read channel IDs.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
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
        var transit = mux.OpenMessageTransit(writeChannelId, readChannelId, sendTypeInfo, receiveTypeInfo, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Opens a send-only message transit using AOT-safe JsonTypeInfo.
    /// Returns immediately in pending state.
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
    /// Returns immediately in pending state.
    /// </summary>
    public static MessageTransit<object, TReceive> AcceptReceiveOnlyMessageTransit<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var readChannel = mux.AcceptChannel(channelId);
        return new MessageTransit<object, TReceive>(null, readChannel, null, receiveTypeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using AOT-safe JsonTypeInfo.
    /// Waits until the channel is ready before returning.
    /// </summary>
    public static async Task<MessageTransit<object, TReceive>> AcceptReceiveOnlyMessageTransitAsync<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptReceiveOnlyMessageTransit(channelId, receiveTypeInfo, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    #endregion

    #region Reflection-based (JsonSerializerOptions)

    /// <summary>
    /// Opens a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<TSend, TReceive> OpenMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var (writeChannel, readChannel) = RegisterPair(mux, channelId + OutboundSuffix, channelId + InboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Waits until both channels are ready before returning.
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
        var transit = mux.OpenMessageTransit<TSend, TReceive>(channelId, jsonOptions, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Accepts a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<TSend, TReceive> AcceptMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var (writeChannel, readChannel) = RegisterPair(mux, channelId + InboundSuffix, channelId + OutboundSuffix);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a message transit using reflection-based JSON serialization. Not AOT-compatible.
    /// Waits until both channels are ready before returning.
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
        var transit = mux.AcceptMessageTransit<TSend, TReceive>(channelId, jsonOptions, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Opens a message transit with the same type for send and receive using reflection. Not AOT-compatible.
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<T, T> OpenMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
        => mux.OpenMessageTransit<T, T>(channelId, jsonOptions, maxMessageSize);

    /// <summary>
    /// Opens a message transit with the same type for send and receive using reflection. Not AOT-compatible.
    /// Waits until both channels are ready before returning.
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
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<T, T> AcceptMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
        => mux.AcceptMessageTransit<T, T>(channelId, jsonOptions, maxMessageSize);

    /// <summary>
    /// Accepts a message transit with the same type for send and receive using reflection. Not AOT-compatible.
    /// Waits until both channels are ready before returning.
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
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<TSend, TReceive> OpenMessageTransit<TSend, TReceive>(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        JsonSerializerOptions? jsonOptions,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var (writeChannel, readChannel) = RegisterPair(mux, writeChannelId, readChannelId);
        return new MessageTransit<TSend, TReceive>(writeChannel, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Opens a message transit with explicit channel IDs using reflection. Not AOT-compatible.
    /// Waits until both channels are ready before returning.
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
        var transit = mux.OpenMessageTransit<TSend, TReceive>(writeChannelId, readChannelId, jsonOptions, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    /// <summary>
    /// Opens a send-only message transit using reflection. Not AOT-compatible.
    /// Returns immediately in pending state.
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
    /// Returns immediately in pending state.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes JsonTypeInfo for AOT compatibility.")]
    public static MessageTransit<object, TReceive> AcceptReceiveOnlyMessageTransit<TReceive>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var readChannel = mux.AcceptChannel(channelId);
        return new MessageTransit<object, TReceive>(null, readChannel, jsonOptions, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only message transit using reflection. Not AOT-compatible.
    /// Waits until the channel is ready before returning.
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
        var transit = mux.AcceptReceiveOnlyMessageTransit<TReceive>(channelId, jsonOptions, maxMessageSize);
        try
        {
            await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transit;
    }

    #endregion

    private static void ValidateBaseChannelId(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        if (channelId.Contains(OutboundSuffix, StringComparison.Ordinal) ||
            channelId.Contains(InboundSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Base channel ID must not contain reserved suffix sequences \"{OutboundSuffix}\" or \"{InboundSuffix}\".",
                nameof(channelId));
        }
    }

    // Atomic registration of the write+read channel pair via the multiplexer's
    // TryRegisterChannels primitive. Either both channels are registered or
    // neither is — no leaked channel id, no phantom INIT frame on the wire.
    private static (IWriteChannel Write, IReadChannel Read) RegisterPair(
        IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId)
    {
        var writeReg = new ChannelRegistration(writeChannelId, ChannelDirection.Outbound);
        var readReg = new ChannelRegistration(readChannelId, ChannelDirection.Inbound);
        ReadOnlySpan<ChannelRegistration> regs = [writeReg, readReg];
        if (!mux.TryRegisterChannels(regs, out var channels))
        {
            throw new MultiplexerException(
                ErrorCode.ChannelExists,
                $"Channel id '{writeChannelId}' or '{readChannelId}' is already in use.");
        }
        return ((IWriteChannel)channels[writeReg], (IReadChannel)channels[readReg]);
    }
}