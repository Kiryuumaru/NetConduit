namespace NetConduit;

/// <summary>
/// An inbound (read-only) channel that receives data from the remote peer.
/// Data arrives as multiplexed frames which are reassembled into a continuous
/// byte stream for reading.
/// </summary>
public interface IReadChannel : IAsyncDisposable, IDisposable
{
    /// <summary>The string identifier for this channel.</summary>
    string ChannelId { get; }

    /// <summary>Current lifecycle state.</summary>
    ChannelState State { get; }

    /// <summary>True after the channel has been confirmed by the remote side. Stays true forever.</summary>
    bool IsReady { get; }

    /// <summary>True when the underlying transport is active. False during disconnects/reconnection.</summary>
    bool IsConnected { get; }

    /// <summary>Priority level of this channel.</summary>
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
    /// Read data from the channel into the provided buffer.
    /// Returns the number of bytes read, or 0 when the channel is closed (EOF).
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Gracefully close the channel.
    /// </summary>
    ValueTask CloseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns this channel as a <see cref="Stream"/> for interop with stream-based APIs.
    /// </summary>
    Stream AsStream();
}
