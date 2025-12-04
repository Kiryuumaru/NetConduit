using System.Collections.Concurrent;

namespace NetConduit.Internal;

/// <summary>
/// Tracks byte positions and buffers data for reconnection support.
/// Ensures no data is lost or duplicated during reconnection.
/// </summary>
internal sealed class ChannelSyncState
{
    private readonly int _maxBufferSize;
    private readonly object _lock = new();
    
    // For write channels: track what we've sent and buffer for replay
    private long _bytesSent;      // Total bytes sent (next byte position to send)
    private long _bytesAcked;     // Bytes acknowledged by receiver
    private readonly List<BufferedSegment> _sendBuffer = new();
    private long _bufferedBytes;
    
    // For read channels: track what we've received
    private long _bytesReceived;  // Total bytes received

    public ChannelSyncState(int maxBufferSize)
    {
        _maxBufferSize = maxBufferSize;
    }

    /// <summary>Total bytes sent on this channel.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>Bytes acknowledged by remote (for write channels).</summary>
    public long BytesAcked => Volatile.Read(ref _bytesAcked);

    /// <summary>Total bytes received on this channel.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>
    /// Records data being sent and buffers it for potential replay.
    /// Returns the byte offset where this data starts.
    /// </summary>
    public long RecordSend(byte[] data)
    {
        lock (_lock)
        {
            var startOffset = _bytesSent;
            _bytesSent += data.Length;
            
            // Only buffer if reconnection is possible
            if (_maxBufferSize > 0)
            {
                // Trim old segments if we exceed buffer size
                while (_bufferedBytes + data.Length > _maxBufferSize && _sendBuffer.Count > 0)
                {
                    var old = _sendBuffer[0];
                    _sendBuffer.RemoveAt(0);
                    _bufferedBytes -= old.Data.Length;
                    // Update ack to reflect trimmed data (can't replay it anymore)
                    _bytesAcked = Math.Max(_bytesAcked, old.StartOffset + old.Data.Length);
                }
                
                _sendBuffer.Add(new BufferedSegment(startOffset, data));
                _bufferedBytes += data.Length;
            }
            
            return startOffset;
        }
    }

    /// <summary>
    /// Acknowledges receipt of data up to the given byte position.
    /// Removes acknowledged data from the buffer.
    /// </summary>
    public void Acknowledge(long bytePosition)
    {
        lock (_lock)
        {
            if (bytePosition <= _bytesAcked)
                return;
                
            _bytesAcked = bytePosition;
            
            // Remove fully acknowledged segments
            while (_sendBuffer.Count > 0)
            {
                var segment = _sendBuffer[0];
                if (segment.StartOffset + segment.Data.Length <= _bytesAcked)
                {
                    _sendBuffer.RemoveAt(0);
                    _bufferedBytes -= segment.Data.Length;
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Records bytes received on this channel.
    /// </summary>
    public void RecordReceive(int byteCount)
    {
        Interlocked.Add(ref _bytesReceived, byteCount);
    }

    /// <summary>
    /// Gets all unacknowledged data starting from the given byte position for replay.
    /// </summary>
    public byte[] GetUnacknowledgedDataFrom(long fromBytePosition)
    {
        lock (_lock)
        {
            if (fromBytePosition >= _bytesSent)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();
            
            foreach (var segment in _sendBuffer)
            {
                var segmentEnd = segment.StartOffset + segment.Data.Length;
                
                if (segmentEnd <= fromBytePosition)
                    continue; // This segment is before our start position
                    
                if (segment.StartOffset >= fromBytePosition)
                {
                    // Entire segment is after start position
                    ms.Write(segment.Data);
                }
                else
                {
                    // Partial segment - start is before our position
                    var skip = (int)(fromBytePosition - segment.StartOffset);
                    ms.Write(segment.Data, skip, segment.Data.Length - skip);
                }
            }
            
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Sets the bytes received position (used during reconnection sync).
    /// </summary>
    public void SetBytesReceived(long position)
    {
        Volatile.Write(ref _bytesReceived, position);
    }

    /// <summary>
    /// Sets the acknowledged byte position (used during reconnection sync).
    /// </summary>
    public void SetBytesAcked(long position)
    {
        Acknowledge(position);
    }

    private sealed class BufferedSegment
    {
        public long StartOffset { get; }
        public byte[] Data { get; }

        public BufferedSegment(long startOffset, byte[] data)
        {
            StartOffset = startOffset;
            Data = data;
        }
    }
}
