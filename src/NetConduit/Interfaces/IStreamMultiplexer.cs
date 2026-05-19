using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Models;

namespace NetConduit.Interfaces;

/// <summary>
/// A transport-agnostic stream multiplexer that creates multiple virtual channels
/// over a single bidirectional stream.
/// </summary>
public interface IStreamMultiplexer : IAsyncDisposable
{
    /// <summary>The multiplexer configuration.</summary>
    MultiplexerOptions Options { get; }

    /// <summary>Session-level statistics.</summary>
    MultiplexerStats Stats { get; }

    /// <summary>True after the first successful connection and handshake. Stays true forever.</summary>
    bool IsReady { get; }

    /// <summary>Whether the transport is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Whether the multiplexer is running (started and not disposed).</summary>
    bool IsRunning { get; }

    /// <summary>Whether a graceful shutdown is in progress.</summary>
    bool IsShuttingDown { get; }

    /// <summary>The local session identity.</summary>
    Guid SessionId { get; }

    /// <summary>The remote peer's session identity.</summary>
    Guid RemoteSessionId { get; }

    /// <summary>String IDs of all currently active channels.</summary>
    IReadOnlyCollection<string> ActiveChannelIds { get; }

    /// <summary>Number of currently active channels.</summary>
    int ActiveChannelCount { get; }

    /// <summary>Reason for the last disconnection, if applicable.</summary>
    DisconnectReason? DisconnectReason { get; }

    /// <summary>Raised once when the multiplexer first becomes ready. Never fires again.</summary>
    event EventHandler? Ready;

    /// <summary>Raised when an outbound channel is opened locally.</summary>
    event EventHandler<ChannelEventArgs>? ChannelOpened;

    /// <summary>Raised when an inbound channel is confirmed by the remote side.</summary>
    event EventHandler<ChannelEventArgs>? ChannelAccepted;

    /// <summary>Raised when a channel is closed.</summary>
    event EventHandler<ChannelClosedEventArgs>? ChannelClosed;

    /// <summary>Raised when an error occurs.</summary>
    event EventHandler<Events.ErrorEventArgs>? Error;

    /// <summary>Raised when the transport is disconnected.</summary>
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised each time the transport connects (initial or reconnect).</summary>
    event EventHandler? Connected;

    /// <summary>Raised when a reconnection attempt begins.</summary>
    event EventHandler<ReconnectingEventArgs>? Reconnecting;

    /// <summary>Start the multiplexer (handshake, read/write loops).</summary>
    void Start();

    /// <summary>Wait until the multiplexer is ready to open/accept channels.</summary>
    Task WaitForReadyAsync(CancellationToken ct = default);

    /// <summary>Open a new outbound channel with the given options.</summary>
    IWriteChannel OpenChannel(ChannelOptions options);

    /// <summary>Accept an inbound channel with the given ID. Returns immediately in pending state.</summary>
    IReadChannel AcceptChannel(string channelId);

    /// <summary>Accept all inbound channels as they arrive.</summary>
    IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Accept only inbound channels whose ID starts with <paramref name="channelIdPrefix"/>.
    /// Channels matching the prefix are routed exclusively to this enumeration and
    /// are NOT yielded by the unfiltered <see cref="AcceptChannelsAsync(CancellationToken)"/>
    /// overload. Useful when an overlay protocol (e.g. mesh routing) shares the
    /// multiplexer with the host application: the overlay subscribes to its
    /// reserved prefix, and the host application iterates the unfiltered overload
    /// to receive only its own channels.
    /// </summary>
    IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(string channelIdPrefix, CancellationToken ct = default);

    /// <summary>Get an outbound channel by its ID, or null if not found.</summary>
    IWriteChannel? GetWriteChannel(string channelId);

    /// <summary>Get an inbound channel by its ID, or null if it hasn't arrived yet.</summary>
    IReadChannel? GetReadChannel(string channelId);

    /// <summary>Initiate graceful shutdown (GoAway).</summary>
    ValueTask GoAwayAsync(CancellationToken ct = default);

    /// <summary>Request an immediate flush of pending writes to the transport.</summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
