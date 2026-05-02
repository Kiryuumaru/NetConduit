namespace NetConduit.Internal;

/// <summary>
/// Tracks bytes received on a read channel for reconnection position sync.
/// </summary>
internal sealed class ChannelSyncState
{
    private long _bytesReceived;

    /// <summary>Total bytes received on this channel.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>
    /// Records bytes received on this channel.
    /// </summary>
    public void RecordReceive(int byteCount)
    {
        Interlocked.Add(ref _bytesReceived, byteCount);
    }
}
