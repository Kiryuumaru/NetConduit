using NetConduit.Enums;
using NetConduit.Exceptions;
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
    {
        ArgumentNullException.ThrowIfNull(mux);

        var defaults = mux.Options.DefaultChannelOptions;
        return mux.OpenChannel(new ChannelOptions
        {
            ChannelId = channelId,
            Priority = defaults.Priority,
            SlabSize = defaults.SlabSize,
            SendTimeout = defaults.SendTimeout,
        });
    }

    /// <summary>
    /// Open a new outbound channel and wait until it is ready.
    /// Equivalent to OpenChannel + WaitForReadyAsync.
    /// </summary>
    public static async Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.OpenChannel(channelId);
        try
        {
            await channel.WaitForReadyAsync(ct).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            // WaitForReadyAsync can throw on cancellation, peer rejection,
            // mux disposal, or transport failure. Without this dispose, the channel
            // remains registered in ChannelRegistry — its slab, wire-index, and
            // channelId stay permanently allocated until the mux is disposed.
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Open a new outbound channel with options and wait until it is ready.
    /// Equivalent to OpenChannel + WaitForReadyAsync.
    /// </summary>
    public static async Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux, ChannelOptions options, CancellationToken ct = default)
    {
        var channel = mux.OpenChannel(options);
        try
        {
            await channel.WaitForReadyAsync(ct).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Accept an inbound channel and wait until it is ready.
    /// Equivalent to AcceptChannel + WaitForReadyAsync.
    /// </summary>
    public static async ValueTask<IReadChannel> AcceptChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.AcceptChannel(channelId);
        try
        {
            await channel.WaitForReadyAsync(ct).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Atomically register a write+read channel pair via the multiplexer's
    /// TryRegisterChannels primitive. Either both channels are registered or
    /// neither is — no leaked channel id, no phantom INIT frame on the wire.
    /// Maps a <c>false</c> return (id collision only, per contract) to
    /// <see cref="MultiplexerException"/> with <see cref="ErrorCode.ChannelExists"/>.
    /// Validation failures propagate from TryRegisterChannels as documented there.
    /// </summary>
    public static (IWriteChannel Write, IReadChannel Read) RegisterChannelPair(
        this IStreamMultiplexer mux,
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
