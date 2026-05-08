using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;

namespace NetConduit.Internal;

/// <summary>
/// 8-byte frame header: [Channel:2B BE][Flags:1B][Reserved:1B][Length:4B BE]
/// </summary>
internal readonly struct FrameHeader
{
    internal const int Size = FrameConstants.HeaderSize;

    internal readonly ushort ChannelIndex;
    internal readonly FrameFlags Flags;
    internal readonly int PayloadLength;

    internal FrameHeader(ushort channelIndex, FrameFlags flags, int payloadLength)
    {
        ChannelIndex = channelIndex;
        Flags = flags;
        PayloadLength = payloadLength;
    }

    internal static FrameHeader Parse(ReadOnlySpan<byte> source)
    {
        ushort channelIndex = BinaryPrimitives.ReadUInt16BigEndian(source);
        var flags = (FrameFlags)source[2];
        // source[3] is reserved
        int payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return new FrameHeader(channelIndex, flags, payloadLength);
    }

    internal static void WriteTo(Span<byte> destination, ushort channelIndex, FrameFlags flags, int payloadLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, channelIndex);
        destination[2] = (byte)flags;
        destination[3] = 0; // reserved
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)payloadLength);
    }

    internal int FrameSize => Size + PayloadLength;
}
