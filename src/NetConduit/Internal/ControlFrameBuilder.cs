using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;

namespace NetConduit.Internal;

/// <summary>
/// Pure byte-composition helpers for control-channel frames sent by
/// <see cref="StreamMultiplexer"/>. The helpers own only header+payload
/// assembly into a returned <see cref="byte"/>[]; callers retain
/// responsibility for the <c>ControlChannel</c> null check and for
/// passing the buffer to <c>WriteRawFrame</c>.
/// </summary>
internal static class ControlFrameBuilder
{
    private const int AckPayloadSize = 8;

    /// <summary>
    /// Builds a control-channel frame (channel index 0, flags Ctrl) carrying
    /// the supplied payload bytes.
    /// </summary>
    internal static byte[] BuildControlFrame(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        int frameSize = FrameHeader.Size + payload.Length;
        byte[] frame = new byte[frameSize];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, flags, payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(frame.AsSpan(FrameHeader.Size));
        return frame;
    }

    /// <summary>
    /// Builds an ACK frame for the supplied channel index carrying the consumed
    /// position as a big-endian uint64 payload.
    /// </summary>
    internal static byte[] BuildAckFrame(uint channelIndex, ulong consumedPosition)
    {
        byte[] frame = new byte[FrameHeader.Size + AckPayloadSize];
        FrameHeader.WriteTo(frame, channelIndex, FrameFlags.Ack, AckPayloadSize);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(FrameHeader.Size, AckPayloadSize), consumedPosition);
        return frame;
    }
}
