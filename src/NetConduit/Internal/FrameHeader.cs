using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;

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
        if (channelIndex == ChannelConstants.Reserved)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Reserved channel index 0x{ChannelConstants.Reserved:X4} is not valid in frame headers.");

        byte rawFlags = source[2];
        if (rawFlags > (byte)FrameFlags.Ctrl)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Unknown frame flags byte: 0x{rawFlags:X2}");

        if (source[3] != 0)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Reserved header byte must be zero: 0x{source[3]:X2}");

        var flags = (FrameFlags)rawFlags;
        uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (rawLength > (uint)FrameConstants.MaxFramePayloadSize)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Frame payload exceeds maximum: {rawLength} > {FrameConstants.MaxFramePayloadSize}");
        return new FrameHeader(channelIndex, flags, (int)rawLength);
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
