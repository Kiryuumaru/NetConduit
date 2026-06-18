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

    /// <summary>
    /// Raised when the remote peer closes a channel via a FIN frame
    /// (<see cref="ChannelCloseReason.RemoteFin"/>). This is a partial close stream:
    /// it does <b>not</b> fire for <see cref="ChannelCloseReason.LocalClose"/>,
    /// <see cref="ChannelCloseReason.RemoteError"/>,
    /// <see cref="ChannelCloseReason.TransportFailed"/>, or
    /// <see cref="ChannelCloseReason.MuxDisposed"/>. For a complete per-channel
    /// close stream with reason information, subscribe to
    /// <see cref="IChannel.Closed"/> on each channel instance.
    /// </summary>
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
    /// <remarks>
    /// An exact-id accept always takes priority over a matching prefix subscription
    /// registered through <see cref="AcceptChannelsAsync"/>. If a caller awaits
    /// <c>AcceptChannel("x/specific")</c> while another caller is enumerating
    /// <c>AcceptChannelsAsync("x/")</c>, the inbound channel <c>"x/specific"</c>
    /// is delivered to the exact-id waiter; the prefix subscription does not
    /// observe it. See <see cref="AcceptChannelsAsync"/> for the full dispatch order.
    /// </remarks>
    IReadChannel AcceptChannel(string channelId);

    /// <summary>
    /// Accept inbound channels as they arrive. When <paramref name="channelIdPrefix"/>
    /// is <c>null</c>, yields every inbound channel not claimed by a specific
    /// <see cref="AcceptChannel"/> call. When a prefix is supplied, only channels
    /// whose ID starts with it are yielded; matched channels are routed exclusively
    /// to this enumeration and are NOT yielded by the unfiltered (null prefix)
    /// overload. This lets an overlay protocol (e.g. mesh routing) share the
    /// multiplexer with the host application by subscribing to a reserved prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Prefix routing rules:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Each namespace may have at most one active subscription. A second call
    /// with an equal or mutually-prefixing <paramref name="channelIdPrefix"/>
    /// throws <see cref="Exceptions.MultiplexerException"/> with
    /// <see cref="Enums.ErrorCode.ChannelExists"/> until the existing subscription
    /// is cancelled. This rejects ambiguous routing at registration time rather
    /// than silently shadowing one subscription with another. The overlap check
    /// runs synchronously when this method is invoked (not lazily on first
    /// enumeration), so the call itself — not the <c>await foreach</c> — is what
    /// throws on conflict.
    /// </description></item>
    /// <item><description>
    /// When the consumer cancels <paramref name="ct"/> or disposes the
    /// enumerator, the subscription is released. Channels that were buffered
    /// for the subscription but never consumed are re-routed to the unfiltered
    /// accept stream so the host application can observe them rather than have
    /// them silently dropped, and the prefix becomes available again.
    /// </description></item>
    /// <item><description>
    /// Inbound channels are dispatched in a fixed priority order, which is part
    /// of the public contract: (1) a caller awaiting <see cref="AcceptChannel"/>
    /// for the exact channel id wins first; (2) otherwise, the first matching
    /// prefix subscription wins (longest-or-first matching subscription per the
    /// registration rules above); (3) otherwise, the unfiltered (null-prefix)
    /// enumeration receives the channel as the catch-all.
    /// </description></item>
    /// </list>
    /// </remarks>
    IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(string? channelIdPrefix = null, CancellationToken ct = default);

    /// <summary>
    /// Atomically register a group of channels. Either every registration is committed,
    /// or none is — the multiplexer's registry is left untouched on failure and no INIT
    /// frames are emitted on the wire for any rolled-back outbound registration.
    /// <para>
    /// Outbound registrations require a vacant channel id; any conflict (the id is
    /// already bound to an existing write channel, read channel, or pending accept)
    /// causes the entire call to roll back and return <c>false</c>.
    /// </para>
    /// <para>
    /// Inbound registrations mirror the idempotent semantics of
    /// <see cref="AcceptChannel"/>: if a read channel or pending accept for the same
    /// id already exists, that channel is reused and returned in the result dictionary.
    /// Only a pre-existing outbound binding on the same id is a collision for an
    /// inbound registration. This is essential for composite transit patterns where
    /// the peer's INIT for the inbound id may have arrived before the local batch runs.
    /// </para>
    /// </summary>
    /// <param name="registrations">
    /// The channels to register. Each entry specifies a channel id, a direction
    /// (<see cref="Enums.ChannelDirection.Outbound"/> = open locally,
    /// <see cref="Enums.ChannelDirection.Inbound"/> = accept locally), and optional
    /// per-channel options (consulted only for outbound registrations). The same
    /// (<c>ChannelId</c>, <c>Direction</c>) pair may not appear twice in a single batch.
    /// </param>
    /// <param name="channels">
    /// On success, a dictionary mapping each input registration to the created
    /// <see cref="IChannel"/> (cast to <see cref="IWriteChannel"/> for outbound or
    /// <see cref="IReadChannel"/> for inbound by the caller). On failure, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if every channel was registered. <c>false</c> if any channel id was
    /// already in use; in that case the registry is restored to its pre-call state.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The multiplexer has not been started, or shutdown has been initiated.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="registrations"/> is empty, contains a null channel id, contains
    /// an invalid channel id (empty or longer than the maximum permitted UTF-8 byte
    /// length), contains a duplicate (<c>ChannelId</c>, <c>Direction</c>) pair, or an
    /// outbound registration carries a <see cref="Models.ChannelOptions"/> whose
    /// <see cref="Models.ChannelOptions.ChannelId"/> disagrees with the registration's
    /// own <c>ChannelId</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An outbound registration carries options with an out-of-range <c>SlabSize</c>.
    /// </exception>
    bool TryRegisterChannels(
        ReadOnlySpan<ChannelRegistration> registrations,
        out IReadOnlyDictionary<ChannelRegistration, IChannel> channels);

    /// <summary>Get an outbound channel by its ID, or null if not found.</summary>
    IWriteChannel? GetWriteChannel(string channelId);

    /// <summary>Get an inbound channel by its ID, or null if it hasn't arrived yet.</summary>
    IReadChannel? GetReadChannel(string channelId);

    /// <summary>Initiate graceful shutdown (GoAway).</summary>
    ValueTask GoAwayAsync(CancellationToken ct = default);

    /// <summary>Request an immediate flush of pending writes to the transport.</summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
