namespace NetConduit.Internal;

/// <summary>
/// Per-channel replay-base position carried in the reconnect handshake.
/// The reader advertises <see cref="FrameBytesReceived"/> for each of its
/// active read channels; the peer's writer uses that value to rewind its
/// own <c>_ackedPos</c> to the byte the receiver actually delivered, so
/// the post-reconnect replay does not duplicate-deliver bytes that crossed
/// the wire under a lost ACK frame.
/// </summary>
internal readonly record struct ChannelReplayPosition(uint ChannelIndex, long FrameBytesReceived);
