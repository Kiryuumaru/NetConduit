using System.Buffers;

namespace NetConduit.Internal;

/// <summary>
/// Mux-level ring buffer for reconnection replay.
/// Records framed data (header + payload) as it's written to TCP.
/// All recording happens at the drain point (already serialized by _streamLock).
/// </summary>
internal sealed class MuxRingBuffer
{
    private readonly int _capacity;
    private byte[] _buffer;
    private int _writePos;
    private int _used;
    private long _totalBytesWritten; // mux-level sequence number
    private long _bytesAcked;        // remote has consumed up to here

    internal MuxRingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        _writePos = 0;
        _used = 0;
        _totalBytesWritten = 0;
        _bytesAcked = 0;
    }

    /// <summary>Total bytes written to the ring (mux sequence number).</summary>
    internal long TotalBytesWritten => Volatile.Read(ref _totalBytesWritten);

    /// <summary>Bytes acknowledged by remote.</summary>
    internal long BytesAcked => Volatile.Read(ref _bytesAcked);

    /// <summary>
    /// Records data that was just written to TCP.
    /// Called from drain thread while _streamLock is held — no additional locking needed.
    /// </summary>
    internal void Record(ReadOnlySpan<byte> data)
    {
        var dataLen = data.Length;
        if (dataLen == 0) return;

        var ringLen = _capacity;

        // If data exceeds ring capacity, only keep the tail
        if (dataLen >= ringLen)
        {
            data[^ringLen..].CopyTo(_buffer);
            _writePos = 0;
            _used = ringLen;
            _totalBytesWritten += dataLen;
            // Advance ack floor if ring wrapped past it
            var ringStart = _totalBytesWritten - ringLen;
            if (_bytesAcked < ringStart)
                _bytesAcked = ringStart;
            return;
        }

        // Write into ring, wrapping if needed
        var firstChunk = Math.Min(dataLen, ringLen - _writePos);
        data[..firstChunk].CopyTo(_buffer.AsSpan(_writePos));
        if (firstChunk < dataLen)
            data[firstChunk..].CopyTo(_buffer.AsSpan(0));

        _writePos = (_writePos + dataLen) % ringLen;
        _used = Math.Min(_used + dataLen, ringLen);
        _totalBytesWritten += dataLen;

        // Advance ack floor if ring wrapped past it
        var startOffset = _totalBytesWritten - _used;
        if (_bytesAcked < startOffset)
            _bytesAcked = startOffset;
    }

    /// <summary>
    /// Records data from a ReadOnlySequence (multi-segment pipe output).
    /// Called from drain thread while _streamLock is held.
    /// </summary>
    internal void Record(ReadOnlySequence<byte> data)
    {
        if (data.IsSingleSegment)
        {
            Record(data.FirstSpan);
            return;
        }

        foreach (var segment in data)
        {
            Record(segment.Span);
        }
    }

    /// <summary>
    /// Acknowledges that remote has consumed up to this sequence position.
    /// </summary>
    internal void Acknowledge(long position)
    {
        if (position <= _bytesAcked) return;
        _bytesAcked = position;

        // Trim used count based on ack
        var ringStart = _totalBytesWritten - _used;
        if (position > ringStart)
        {
            var trimmed = (int)(position - ringStart);
            _used -= trimmed;
            if (_used < 0) _used = 0;
        }
    }

    /// <summary>
    /// Gets all unacknowledged data from the given sequence position for replay.
    /// Returns empty if the position is beyond what's available in the ring.
    /// </summary>
    internal byte[] GetUnacknowledgedDataFrom(long fromPosition)
    {
        if (_used == 0 || fromPosition >= _totalBytesWritten)
            return Array.Empty<byte>();

        var ringStart = _totalBytesWritten - _used;
        var effectiveFrom = Math.Max(fromPosition, ringStart);
        var available = (int)(_totalBytesWritten - effectiveFrom);
        if (available <= 0)
            return Array.Empty<byte>();

        var result = new byte[available];
        var ringLen = _capacity;

        // Calculate read position
        var offsetInRing = (int)(effectiveFrom - ringStart);
        var readPos = (_writePos - _used + offsetInRing + ringLen) % ringLen;

        // Copy from ring, handling wrap
        var firstChunk = Math.Min(available, ringLen - readPos);
        _buffer.AsSpan(readPos, firstChunk).CopyTo(result);
        if (firstChunk < available)
            _buffer.AsSpan(0, available - firstChunk).CopyTo(result.AsSpan(firstChunk));

        return result;
    }

    /// <summary>
    /// Returns the buffer to ArrayPool.
    /// </summary>
    internal void Release()
    {
        var buf = _buffer;
        _buffer = Array.Empty<byte>();
        _used = 0;
        if (buf.Length > 0)
            ArrayPool<byte>.Shared.Return(buf);
    }
}
