namespace NetConduit.Interfaces;

/// <summary>
/// An outbound (write-only) channel that sends data to the remote peer.
/// Data written to this channel is framed, buffered, and multiplexed over
/// the shared transport stream.
/// </summary>
public interface IWriteChannel : IChannel
{
    /// <summary>
    /// Write data to the channel. The data is framed and queued for transmission
    /// by the multiplexer's write loop.
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
