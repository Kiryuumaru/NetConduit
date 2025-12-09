using System.Buffers.Binary;

namespace NetConduit.Internal;

/// <summary>
/// Frame header structure. 17 bytes: [ChannelId: 4B BE] [Flags: 1B] [Seq: 4B BE] [Length: 4B BE] [Crc32: 4B]
/// CRC32 is computed over [ChannelId, Flags, Seq, Length, Payload] with Crc32 field zeroed.
/// </summary>
internal readonly struct FrameHeader
{
    public const int Size = 17;
    
    /// <summary>Offset of the CRC32 field in the header.</summary>
    public const int Crc32Offset = 13;
    
    // CRC32 lookup table (IEEE polynomial 0xEDB88320)
    private static readonly uint[] Crc32Table = GenerateCrc32Table();
    
    private static uint[] GenerateCrc32Table()
    {
        var table = new uint[256];
        const uint polynomial = 0xEDB88320u;
        
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }
    
    public readonly uint ChannelId;
    public readonly FrameFlags Flags;
    public readonly uint Seq;
    public readonly uint Length;
    public readonly uint Crc32;

    public FrameHeader(uint channelId, FrameFlags flags, uint seq, uint length, uint crc32 = 0)
    {
        ChannelId = channelId;
        Flags = flags;
        Seq = seq;
        Length = length;
        Crc32 = crc32;
    }

    /// <summary>
    /// Creates a header with computed CRC32 for the given payload.
    /// </summary>
    public static FrameHeader CreateWithCrc(uint channelId, FrameFlags flags, uint seq, ReadOnlySpan<byte> payload)
    {
        var length = (uint)payload.Length;
        var crc = ComputeCrc32(channelId, flags, seq, length, payload);
        return new FrameHeader(channelId, flags, seq, length, crc);
    }

    /// <summary>
    /// Computes CRC32 over the header fields (with CRC field = 0) and payload.
    /// </summary>
    public static uint ComputeCrc32(uint channelId, FrameFlags flags, uint seq, uint length, ReadOnlySpan<byte> payload)
    {
        // Build buffer: header without CRC (13 bytes) + payload
        Span<byte> headerPart = stackalloc byte[Crc32Offset];
        BinaryPrimitives.WriteUInt32BigEndian(headerPart, channelId);
        headerPart[4] = (byte)flags;
        BinaryPrimitives.WriteUInt32BigEndian(headerPart[5..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(headerPart[9..], length);
        
        // Compute CRC32 using lookup table
        uint crc = 0xFFFFFFFFu;
        
        // Process header bytes
        foreach (var b in headerPart)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        
        // Process payload bytes
        foreach (var b in payload)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Validates CRC32 against the header and payload.
    /// </summary>
    public bool ValidateCrc(ReadOnlySpan<byte> payload)
    {
        var computed = ComputeCrc32(ChannelId, Flags, Seq, Length, payload);
        return computed == Crc32;
    }

    public static FrameHeader Read(ReadOnlySpan<byte> buffer)
    {
        var channelId = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        var flags = (FrameFlags)buffer[4];
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buffer[5..]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer[9..]);
        var crc32 = BinaryPrimitives.ReadUInt32BigEndian(buffer[13..]);
        return new FrameHeader(channelId, flags, seq, length, crc32);
    }

    public void Write(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ChannelId);
        buffer[4] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[5..], Seq);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[9..], Length);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[13..], Crc32);
    }
    
    /// <summary>
    /// Creates a new FrameHeader with the specified CRC32 value.
    /// </summary>
    public FrameHeader WithCrc(uint crc) => new(ChannelId, Flags, Seq, Length, crc);
}
