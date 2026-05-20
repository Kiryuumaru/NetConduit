namespace NetConduit.Interfaces;

/// <summary>
/// An inbound (read-only) channel that receives data from the remote peer.
/// Data arrives as multiplexed frames which are reassembled into a continuous
/// byte stream for reading.
/// </summary>
public interface IReadChannel : IChannel
{
    /// <summary>
    /// Read data from the channel into the provided buffer.
    /// Returns the number of bytes read, or 0 when the channel is closed (EOF).
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}
