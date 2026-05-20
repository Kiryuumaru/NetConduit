using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Models;

namespace NetConduit.Interfaces;

/// <summary>
/// Common surface for all channels (read or write). Provides identity, lifecycle
/// state, statistics, lifecycle events, and graceful close.
/// <para>
/// This interface lets heterogeneous channel collections be expressed uniformly
/// — most notably as the value type of the dictionary returned by
/// <see cref="IStreamMultiplexer.TryRegisterChannels"/>.
/// </para>
/// </summary>
public interface IChannel : IAsyncDisposable, IDisposable
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

    /// <summary>Gracefully close the channel.</summary>
    ValueTask CloseAsync(CancellationToken ct = default);

    /// <summary>Returns this channel as a <see cref="Stream"/> for interop with stream-based APIs.</summary>
    Stream AsStream();
}
