using System.Collections.Concurrent;

namespace NetConduit.Internal;

/// <summary>
/// Tracks byte positions and buffers data for reconnection support.
/// Ensures no data is lost or duplicated during reconnection.
/// Uses a ring buffer to avoid per-frame heap allocations.
/// Recording is lazy: no buffer allocated until StartRecording is called.
/// </summary>
internal sealed class ChannelSyncState
{
    private readonly int _maxBufferSize;
    private readonly object _lock = new();
    
    // For write channels: track what we've sent and buffer for replay
    private long _bytesSent;      // Total bytes sent (next byte position to send)
    private long _bytesAcked;     // Bytes acknowledged by receiver
    
    // Ring buffer for reconnection replay (lazy-allocated)
    private byte[]? _ringBuffer;
    private int _ringWritePos;    // Next write position in ring
    private long _ringStartOffset; // Byte offset of the oldest byte in the ring
    private int _ringUsed;        // Bytes currently stored in ring
    private bool _recording;      // Whether recording is active
    
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
    /// Starts recording sent data for replay. Allocates the ring buffer.
    /// Call this when reconnection support is needed.
    /// </summary>
    public void StartRecording()
    {
        lock (_lock)
        {
            if (_recording || _maxBufferSize <= 0) return;
            _recording = true;
        }
    }

    /// <summary>
    /// Records data being sent and buffers it for potential replay.
    /// Returns the byte offset where this data starts.
    /// No-op if recording is not active.
    /// </summary>
    public long RecordSend(ReadOnlySpan<byte> data)
    {
        if (!_recording)
        {
            return Interlocked.Add(ref _bytesSent, data.Length) - data.Length;
        }
        
        lock (_lock)
        {
            var startOffset = _bytesSent;
            _bytesSent += data.Length;
            
            // Lazy-allocate ring buffer on first write
            if (_ringBuffer is null)
            {
                _ringBuffer = new byte[_maxBufferSize];
                _ringStartOffset = startOffset;
            }
            
            var ring = _ringBuffer;
            var ringLen = ring.Length;
            var dataLen = data.Length;
            
            // If data exceeds ring capacity, only keep the tail
            if (dataLen >= ringLen)
            {
                data[^ringLen..].CopyTo(ring);
                _ringWritePos = 0;
                _ringUsed = ringLen;
                _ringStartOffset = _bytesSent - ringLen;
                _bytesAcked = Math.Max(_bytesAcked, _ringStartOffset);
                return startOffset;
            }
            
            // Write into ring, wrapping if needed
            var firstChunk = Math.Min(dataLen, ringLen - _ringWritePos);
            data[..firstChunk].CopyTo(ring.AsSpan(_ringWritePos));
            if (firstChunk < dataLen)
                data[firstChunk..].CopyTo(ring.AsSpan(0));
            
            _ringWritePos = (_ringWritePos + dataLen) % ringLen;
            _ringUsed = Math.Min(_ringUsed + dataLen, ringLen);
            _ringStartOffset = _bytesSent - _ringUsed;
            
            // If ring wrapped, update ack to reflect lost data
            if (_bytesAcked < _ringStartOffset)
                _bytesAcked = _ringStartOffset;
            
            return startOffset;
        }
    }

    /// <summary>
    /// Acknowledges receipt of data up to the given byte position.
    /// </summary>
    public void Acknowledge(long bytePosition)
    {
        lock (_lock)
        {
            if (bytePosition <= _bytesAcked)
                return;
                
            _bytesAcked = bytePosition;
            
            if (_recording && _ringUsed > 0)
            {
                // Trim acknowledged data from the ring
                var ackedInRing = bytePosition - _ringStartOffset;
                if (ackedInRing > 0)
                {
                    var trimBytes = (int)Math.Min(ackedInRing, _ringUsed);
                    _ringUsed -= trimBytes;
                    _ringStartOffset += trimBytes;
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
            if (!_recording || _ringUsed == 0 || fromBytePosition >= _bytesSent)
                return Array.Empty<byte>();

            // Clamp to what we actually have in the ring
            var effectiveFrom = Math.Max(fromBytePosition, _ringStartOffset);
            var available = (int)(_bytesSent - effectiveFrom);
            if (available <= 0)
                return Array.Empty<byte>();
            
            var result = new byte[available];
            var ringLen = _ringBuffer!.Length;
            
            // Calculate read position in ring
            var offsetInRing = (int)(effectiveFrom - _ringStartOffset);
            var readPos = (_ringWritePos - _ringUsed + offsetInRing + ringLen) % ringLen;
            
            // Copy from ring, handling wrap
            var firstChunk = Math.Min(available, ringLen - readPos);
            _ringBuffer.AsSpan(readPos, firstChunk).CopyTo(result);
            if (firstChunk < available)
                _ringBuffer.AsSpan(0, available - firstChunk).CopyTo(result.AsSpan(firstChunk));
            
            return result;
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
}
