using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Models;

namespace NetConduit.Interfaces;

/// <summary>
/// An outbound (write-only) channel that sends data to the remote peer.
/// Data written to this channel is framed, buffered, and multiplexed over
/// the shared transport stream.
/// </summary>
public interface IWriteChannel : IAsyncDisposable, IDisposable
{
    /// <summary>The string identifier for this channel.</summary>
    string ChannelId { get; }

    /// <summary>Current lifecycle state.</summary>
    ChannelState State { get; }

    /// <summary>True after the channel has been confirmed by the remote side. Stays true forever.</summary>
    bool IsReady { get; }

    /// <summary>True when the underlying transport is active. False during disconnects/reconnection.</summary>
    bool IsConnected { get; }

    /// <summary>Priority level used by the writer thread for ordering.</summary>
    ChannelPriority Priority { get; }

    /// <summary>Per-channel statistics.</summary>
    ChannelStats Stats { get; }

    /// <summary>Reason the channel was closed, if applicable.</summary>
    ChannelCloseReason? CloseReason { get; }

    /// <summary>Exception that caused the close, if applicable.</summary>
    Exception? CloseException { get; }

    /// <summary>Raised once when the channel first becomes ready. Never fires again.</summary>
    event EventHandler? Ready;

    /// <summary>Raised each time the channel's underlying transport connects (including reconnects).</summary>
    event EventHandler? Connected;

    /// <summary>Raised each time the channel's underlying transport disconnects.</summary>
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised when the channel is closed.</summary>
    event EventHandler<ChannelCloseEventArgs>? Closed;

    /// <summary>Wait until the channel is confirmed ready by the remote side.</summary>
    Task WaitForReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// Write data to the channel. The data is framed and queued for transmission
    /// by the multiplexer's write loop.
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Gracefully close the channel by sending a FIN frame.
    /// Any pending data is flushed before the FIN is sent.
    /// </summary>
    ValueTask CloseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns this channel as a <see cref="Stream"/> for interop with stream-based APIs.
    /// </summary>
    Stream AsStream();
}
