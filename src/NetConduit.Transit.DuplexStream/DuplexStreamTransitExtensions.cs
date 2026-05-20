using NetConduit.Interfaces;

namespace NetConduit.Transit.DuplexStream;

/// <summary>
/// Extension methods on <see cref="IStreamMultiplexer"/> for creating <see cref="DuplexStreamTransit"/> instances.
/// <para>
/// Duplex transits use a naming convention where two simplex channels form a bidirectional pair.
/// Given a base <c>channelId</c>, the outbound (write) channel is named <c>"{channelId}&gt;&gt;"</c>
/// and the inbound (read) channel is named <c>"{channelId}&lt;&lt;"</c>. The counterpart
/// (<see cref="AcceptDuplexStreamAsync(IStreamMultiplexer, string, CancellationToken)"/>)
/// reverses the roles: it reads from <c>"{channelId}&gt;&gt;"</c> and writes to <c>"{channelId}&lt;&lt;"</c>.
/// </para>
/// <para>
/// The base <c>channelId</c> must not itself contain the suffix sequences <c>"&gt;&gt;"</c> or <c>"&lt;&lt;"</c>,
/// as this would produce ambiguous composite channel names. Callers are responsible for choosing
/// base channel IDs that do not contain these reserved sequences.
/// </para>
/// </summary>
public static class DuplexStreamTransitExtensions
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
    /// Opens a write channel and accepts a read channel, then wraps them as a bidirectional Stream.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Returns immediately in pending state. Use <see cref="ITransit.WaitForReadyAsync"/> to wait for readiness.
    /// </summary>
    public static DuplexStreamTransit OpenDuplexStream(
        this IStreamMultiplexer mux,
        string channelId)
    {
        ValidateBaseChannelId(channelId);
        var writeChannel = mux.OpenChannel(channelId + OutboundSuffix);
        IReadChannel readChannel;
        try
        {
            readChannel = mux.AcceptChannel(channelId + InboundSuffix);
        }
        catch
        {
            SafeDispose(writeChannel);
            throw;
        }
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Opens a write channel and accepts a read channel, then wraps them as a bidirectional Stream.
    /// Uses "{channelId}&gt;&gt;" for writing and "{channelId}&lt;&lt;" for reading.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.OpenDuplexStream(channelId);
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
    /// Accepts a read channel and opens a write channel, then wraps them as a bidirectional Stream.
    /// This is the counterpart to <see cref="OpenDuplexStream(IStreamMultiplexer, string)"/>.
    /// Returns immediately in pending state. Use <see cref="ITransit.WaitForReadyAsync"/> to wait for readiness.
    /// </summary>
    public static DuplexStreamTransit AcceptDuplexStream(
        this IStreamMultiplexer mux,
        string channelId)
    {
        ValidateBaseChannelId(channelId);
        var readChannel = mux.AcceptChannel(channelId + OutboundSuffix);
        IWriteChannel writeChannel;
        try
        {
            writeChannel = mux.OpenChannel(channelId + InboundSuffix);
        }
        catch
        {
            SafeDispose(readChannel);
            throw;
        }
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Accepts a read channel and opens a write channel, then wraps them as a bidirectional Stream.
    /// This is the counterpart to <see cref="OpenDuplexStreamAsync(IStreamMultiplexer, string, CancellationToken)"/>.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<DuplexStreamTransit> AcceptDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptDuplexStream(channelId);
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
    /// Opens a write channel and accepts a read channel with explicit channel IDs,
    /// then wraps them as a bidirectional Stream.
    /// Returns immediately in pending state. Use <see cref="ITransit.WaitForReadyAsync"/> to wait for readiness.
    /// </summary>
    public static DuplexStreamTransit OpenDuplexStream(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId)
    {
        var writeChannel = mux.OpenChannel(writeChannelId);
        IReadChannel readChannel;
        try
        {
            readChannel = mux.AcceptChannel(readChannelId);
        }
        catch
        {
            SafeDispose(writeChannel);
            throw;
        }
        return new DuplexStreamTransit(writeChannel, readChannel);
    }

    /// <summary>
    /// Opens a write channel and accepts a read channel with explicit channel IDs,
    /// then wraps them as a bidirectional Stream.
    /// Waits until both channels are ready before returning.
    /// </summary>
    public static async Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux,
        string writeChannelId,
        string readChannelId,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.OpenDuplexStream(writeChannelId, readChannelId);
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

    // Best-effort cleanup on the exception path. The original exception from the
    // failed second registration must surface to the caller, so a secondary
    // failure during channel disposal is intentionally swallowed.
    private static void SafeDispose(IDisposable channel)
    {
        try { channel.Dispose(); }
        catch { }
    }
}
