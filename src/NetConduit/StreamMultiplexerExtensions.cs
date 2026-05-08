using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit;

/// <summary>
/// Convenience extension methods for IStreamMultiplexer that combine
/// channel creation with readiness waiting.
/// </summary>
public static class StreamMultiplexerExtensions
{
    /// <summary>
    /// Open a new outbound channel with the given ID using default options.
    /// </summary>
    public static IWriteChannel OpenChannel(
        this IStreamMultiplexer mux, string channelId)
        => mux.OpenChannel(new ChannelOptions { ChannelId = channelId });

    /// <summary>
    /// Open a new outbound channel and wait until it is ready.
    /// Equivalent to OpenChannel + WaitForReadyAsync.
    /// </summary>
    public static async Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.OpenChannel(channelId);
        await channel.WaitForReadyAsync(ct);
        return channel;
    }

    /// <summary>
    /// Open a new outbound channel with options and wait until it is ready.
    /// Equivalent to OpenChannel + WaitForReadyAsync.
    /// </summary>
    public static async Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux, ChannelOptions options, CancellationToken ct = default)
    {
        var channel = mux.OpenChannel(options);
        await channel.WaitForReadyAsync(ct);
        return channel;
    }

    /// <summary>
    /// Accept an inbound channel and wait until it is ready.
    /// Equivalent to AcceptChannel + WaitForReadyAsync.
    /// </summary>
    public static async ValueTask<IReadChannel> AcceptChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.AcceptChannel(channelId);
        await channel.WaitForReadyAsync(ct);
        return channel;
    }
}
