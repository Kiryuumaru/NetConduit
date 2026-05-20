using System.Text.Json.Serialization.Metadata;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DeltaMessage;

/// <summary>
/// Extension methods on <see cref="IStreamMultiplexer"/> for creating <see cref="DeltaMessageTransit{T}"/> instances.
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
public static class DeltaMessageTransitExtensions
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

    /// <summary>
    /// Opens a delta message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state.
    /// </summary>
    public static DeltaMessageTransit<T> OpenDeltaMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        var readChannel = mux.AcceptChannel(channelId + InboundSuffix);
        return new DeltaMessageTransit<T>(writeChannel, readChannel, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Opens a delta message transit by opening a write channel and accepting a read channel.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<DeltaMessageTransit<T>> OpenDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.OpenDeltaMessageTransit(channelId, typeInfo, maxMessageSize);
        await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        return transit;
    }

    /// <summary>
    /// Accepts a delta message transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to OpenDeltaMessageTransit.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state.
    /// </summary>
    public static DeltaMessageTransit<T> AcceptDeltaMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        ValidateBaseChannelId(channelId);
        var readChannel = mux.AcceptChannel(channelId + OutboundSuffix);
        var writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        return new DeltaMessageTransit<T>(writeChannel, readChannel, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a delta message transit by accepting a read channel and opening a write channel.
    /// This is the counterpart to OpenDeltaMessageTransitAsync.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<DeltaMessageTransit<T>> AcceptDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptDeltaMessageTransit(channelId, typeInfo, maxMessageSize);
        await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        return transit;
    }

    /// <summary>
    /// Opens a send-only delta message transit for pushing state updates.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state.
    /// </summary>
    public static DeltaMessageTransit<T> OpenSendOnlyDeltaMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var writeChannel = mux.OpenChannel(channelId);
        return new DeltaMessageTransit<T>(writeChannel, null, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only delta message transit for receiving state updates.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Returns immediately in pending state.
    /// </summary>
    public static DeltaMessageTransit<T> AcceptReceiveOnlyDeltaMessageTransit<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024)
    {
        var readChannel = mux.AcceptChannel(channelId);
        return new DeltaMessageTransit<T>(null, readChannel, typeInfo, maxMessageSize);
    }

    /// <summary>
    /// Accepts a receive-only delta message transit for receiving state updates.
    /// Uses AOT-safe JsonTypeInfo for serialization.
    /// Waits until the channel is ready before returning.
    /// </summary>
    public static async Task<DeltaMessageTransit<T>> AcceptReceiveOnlyDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux,
        string channelId,
        JsonTypeInfo<T> typeInfo,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptReceiveOnlyDeltaMessageTransit(channelId, typeInfo, maxMessageSize);
        await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        return transit;
    }

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
}
