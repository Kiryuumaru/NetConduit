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
    /// Called by a channel when it has fully completed its lifecycle
    /// (all frames sent for write channels, disposed for read channels).
    /// The owner unregisters the channel and updates stats.
    /// </summary>
    void NotifyChannelCompleted(ushort channelIndex, string channelId);

    /// <summary>
    /// Sends a position-based ACK frame for the given channel back to the remote.
    /// Used by ReadChannel to inform the remote WriteChannel how far the consumer
    /// has consumed, so PrepareReplay on reconnect skips already-delivered bytes.
    /// </summary>
    void SendAck(ushort channelIndex, ulong consumedPosition);
}
