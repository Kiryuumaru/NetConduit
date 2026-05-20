namespace NetConduit.Events;

/// <summary>
/// Event data raised by <see cref="Interfaces.IStreamMultiplexer.ChannelClosed"/>
/// when the remote peer closes a channel via a FIN frame.
/// </summary>
/// <remarks>
/// This is the mux-level close event. It carries the channel id but no reason —
/// only the remote-FIN case raises it today. For local-close, remote-error,
/// transport-failure, and dispose cases (i.e. the other four values of
/// <see cref="Enums.ChannelCloseReason"/>), subscribe to the channel-instance
/// <see cref="Interfaces.IReadChannel.Closed"/> /
/// <see cref="Interfaces.IWriteChannel.Closed"/> events instead. Those carry
/// <see cref="ChannelCloseEventArgs"/> with a populated
/// <see cref="Enums.ChannelCloseReason"/>.
/// </remarks>
/// <seealso cref="ChannelCloseEventArgs"/>
public sealed class ChannelClosedEventArgs(string channelId, Exception? exception) : EventArgs
{
    /// <summary>The string identifier of the closed channel.</summary>
    public string ChannelId { get; } = channelId;

    /// <summary>
    /// Reserved for forward compatibility. Currently always <c>null</c> —
    /// the only emission path (remote FIN) does not carry an exception.
    /// To observe close exceptions, subscribe to the channel-instance
    /// <see cref="Interfaces.IReadChannel.Closed"/> /
    /// <see cref="Interfaces.IWriteChannel.Closed"/> events.
    /// </summary>
    public Exception? Exception { get; } = exception;
}
