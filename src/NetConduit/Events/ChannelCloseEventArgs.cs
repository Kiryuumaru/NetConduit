using NetConduit.Enums;

namespace NetConduit.Events;

/// <summary>
/// Event data raised by the channel-instance
/// <see cref="Interfaces.IReadChannel.Closed"/> /
/// <see cref="Interfaces.IWriteChannel.Closed"/> events when the channel itself
/// closes for any reason.
/// </summary>
/// <remarks>
/// This is the complete per-channel close stream: every value of
/// <see cref="ChannelCloseReason"/> raises it. The channel id is not duplicated
/// here because the <c>sender</c> argument of the event is the
/// <see cref="Interfaces.IReadChannel"/> / <see cref="Interfaces.IWriteChannel"/>
/// instance, which exposes <c>ChannelId</c>.
/// <para>
/// Not to be confused with <see cref="ChannelClosedEventArgs"/>, which is the
/// mux-level event and currently only fires on remote FIN.
/// </para>
/// </remarks>
/// <seealso cref="ChannelClosedEventArgs"/>
public sealed class ChannelCloseEventArgs(ChannelCloseReason reason, Exception? exception) : EventArgs
{
    /// <summary>The reason the channel was closed.</summary>
    public ChannelCloseReason Reason { get; } = reason;

    /// <summary>The exception that caused the close, if any.</summary>
    public Exception? Exception { get; } = exception;
}
