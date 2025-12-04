using System.Buffers.Binary;

namespace NetConduit.Internal;

/// <summary>
/// Frame header structure. 9 bytes: [ChannelId: 4B BE] [Flags: 1B] [Length: 4B BE]
/// </summary>
internal readonly struct FrameHeader
{
    public const int Size = 9;
    
    public readonly uint ChannelId;
    public readonly FrameFlags Flags;
    public readonly uint Length;

    public FrameHeader(uint channelId, FrameFlags flags, uint length)
    {
        ChannelId = channelId;
        Flags = flags;
        Length = length;
    }

    public static FrameHeader Read(ReadOnlySpan<byte> buffer)
    {
        var channelId = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        var flags = (FrameFlags)buffer[4];
        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer[5..]);
        return new FrameHeader(channelId, flags, length);
    }

    public void Write(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ChannelId);
        buffer[4] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[5..], Length);
    }
}
