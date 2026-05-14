using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.Stream;

/// <summary>
/// Extension methods on <see cref="IStreamMultiplexer"/> for creating <see cref="StreamTransit"/> instances.
/// </summary>
public static class StreamTransitExtensions
{
    /// <summary>
    /// Opens a channel and wraps it as a write-only Stream.
    /// Returns immediately in pending state.
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
    /// Returns immediately in pending state.
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
    /// Returns immediately in pending state. Use <see cref="ITransit.WaitForReadyAsync"/> to wait for readiness.
    /// </summary>
    public static StreamTransit AcceptStream(
        this IStreamMultiplexer mux,
        string channelId)
    {
        var channel = mux.AcceptChannel(channelId);
        return new StreamTransit(channel);
    }

    /// <summary>
    /// Accepts a channel and wraps it as a read-only Stream.
    /// Waits until the channel is ready before returning.
    /// </summary>
    public static async Task<StreamTransit> AcceptStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var transit = mux.AcceptStream(channelId);
        await transit.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        return transit;
    }
}
