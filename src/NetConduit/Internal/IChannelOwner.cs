namespace NetConduit.Internal;

/// <summary>
/// Contract between channels and their owning multiplexer.
/// Channels call back into this interface for routing and lifecycle management.
/// </summary>
internal interface IChannelOwner
{
    /// <summary>Signals the writer loop that a channel has frames ready to send.</summary>
    void NotifyReady(WriteChannel channel);

    /// <summary>
    /// Called by a write channel the first time it transitions to ready
    /// (init-ack received from the remote). The owner raises the public
    /// <c>ChannelOpened</c> event.
    /// </summary>
    void NotifyChannelOpened(string channelId);

    /// <summary>
    /// Called by a channel when it has fully completed its lifecycle
    /// (all frames sent for write channels, disposed for read channels).
    /// The owner unregisters the channel and updates stats.
    /// </summary>
    void NotifyChannelCompleted(ushort channelIndex, string channelId);

    /// <summary>
    /// Called by a pending-accept <see cref="ReadChannel"/> (created via
    /// <c>AcceptChannel(string)</c> but not yet wired to a remote channel
    /// index) when it is disposed before the peer's INIT frame arrives.
    /// The owner removes the channel from the pending-accept map so the
    /// next inbound INIT for the same channel id is treated as a fresh
    /// channel rather than resurrecting the disposed instance.
    /// </summary>
    void NotifyPendingAcceptCancelled(string channelId);

    /// <summary>
    /// Sends a position-based ACK frame for the given channel back to the remote.
    /// Used by ReadChannel to inform the remote WriteChannel how far the consumer
    /// has consumed, so PrepareReplay on reconnect skips already-delivered bytes.
    /// Returns <c>true</c> if the ACK was staged for transmission; <c>false</c> if
    /// the control-channel slab cannot currently fit the frame. A <c>false</c>
    /// return is non-fatal: the receive-side accumulator keeps growing and the
    /// next ACK gate will retry with the latest cumulative position.
    /// </summary>
    bool SendAck(ushort channelIndex, ulong consumedPosition);

    /// <summary>
    /// Called by a channel when one of its public events (Ready / Connected /
    /// Disconnected / Closed) had a user handler throw a non-fatal exception.
    /// The owner forwards the exception to its observability surface (the
    /// multiplexer's Error event). Implementations MUST NOT throw — this is
    /// invoked from inside the channel event raise loop and a throw here would
    /// defeat the multicast safety the channel is trying to provide.
    /// </summary>
    void NotifyEventHandlerException(Exception exception);

    /// <summary>
    /// The maximum frame payload the remote peer will accept on any inbound
    /// channel, as negotiated during the most recent handshake.
    /// <see cref="WriteChannel.WriteAsync"/> clamps every write against this
    /// in addition to its own local slab size so a heterogeneous slab
    /// configuration cannot send a frame the receiver's slab cannot buffer.
    /// </summary>
    int PeerMaxRecvPayload { get; }

    /// <summary>
    /// Live snapshot of the multiplexer's transport-connected state. Read
    /// by <see cref="ChannelBatchRegistrar"/> in Phase 3 — after a fresh
    /// channel has been published into the registry — so the batch-register
    /// path observes the same publish-then-read invariant as
    /// <c>StreamMultiplexer.OpenChannel</c>. Capturing this value at the
    /// caller's entry to <c>TryRegisterChannels</c> instead would create a
    /// race window in which the mux completes its initial handshake between
    /// the entry read and Phase 2 commit, MainLoopAsync's MarkConnected
    /// foreach runs against an empty snapshot, and Phase 3 then skips
    /// MarkConnected on the stale-false snapshot — leaving fresh channels
    /// IsConnected==false forever despite a live transport (fixes #399).
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c> so isolated test fakes that exercise
    /// individual channels without a real multiplexer do not need to
    /// implement this member. <see cref="StreamMultiplexer"/> overrides
    /// the default with the live <c>_isConnected</c> field.
    /// </remarks>
    bool IsTransportConnected => false;
}
