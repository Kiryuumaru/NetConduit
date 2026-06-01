using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;

namespace NetConduit.Internal;

/// <summary>
/// 12-byte frame header: [Channel:4B BE][Flags:1B][Reserved:3B][Length:4B BE]
/// </summary>
internal readonly struct FrameHeader
{
    internal const int Size = FrameConstants.HeaderSize;

    internal readonly uint ChannelIndex;
    internal readonly FrameFlags Flags;
    internal readonly int PayloadLength;

    internal FrameHeader(uint channelIndex, FrameFlags flags, int payloadLength)
    {
        ChannelIndex = channelIndex;
        Flags = flags;
        PayloadLength = payloadLength;
    }

    internal static FrameHeader Parse(ReadOnlySpan<byte> source)
    {
        uint channelIndex = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (channelIndex == ChannelConstants.Reserved)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Reserved channel index 0x{ChannelConstants.Reserved:X8} is not valid in frame headers.");

        byte rawFlags = source[4];
        if (rawFlags > (byte)FrameFlags.Ctrl)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Unknown frame flags byte: 0x{rawFlags:X2}");

        if (source[5] != 0 || source[6] != 0 || source[7] != 0)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Reserved header bytes must be zero: 0x{source[5]:X2}{source[6]:X2}{source[7]:X2}");

        var flags = (FrameFlags)rawFlags;
        uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(source[8..]);
        if (rawLength > (uint)FrameConstants.MaxFramePayloadSize)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Frame payload exceeds maximum: {rawLength} > {FrameConstants.MaxFramePayloadSize}");
        return new FrameHeader(channelIndex, flags, (int)rawLength);
    }

    internal static void WriteTo(Span<byte> destination, uint channelIndex, FrameFlags flags, int payloadLength)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, channelIndex);
        destination[4] = (byte)flags;
        destination[5] = 0; // reserved
        destination[6] = 0; // reserved
        destination[7] = 0; // reserved
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], (uint)payloadLength);
    }

    internal int FrameSize => Size + PayloadLength;
}
