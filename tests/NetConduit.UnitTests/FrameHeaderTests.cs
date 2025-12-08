using System.Buffers.Binary;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

public class FrameHeaderTests
{
    [Fact]
    public void FrameHeader_RoundTrip_PreservesValues()
    {
        var header = new FrameHeader(0x12345678, FrameFlags.Data, 0x11223344, 0xABCDEF01, 0xDEADBEEF);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        var read = FrameHeader.Read(buffer);
        
        Assert.Equal(header.ChannelId, read.ChannelId);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.Seq, read.Seq);
        Assert.Equal(header.Length, read.Length);
        Assert.Equal(header.Crc32, read.Crc32);
    }

    [Theory]
    [InlineData(0u, FrameFlags.Data, 0u, 0u, 0u)]
    [InlineData(uint.MaxValue - 1, FrameFlags.Init, uint.MaxValue, uint.MaxValue, uint.MaxValue)]
    [InlineData(0x80000000, FrameFlags.Fin, 1000, 1024, 0xCAFEBABE)]
    [InlineData(1, FrameFlags.Ack, 999, 65536, 0x12345678)]
    [InlineData(0x7FFFFFFF, FrameFlags.Err, 0, 16 * 1024 * 1024, 0)]
    public void FrameHeader_RoundTrip_AllFlags(uint channelId, FrameFlags flags, uint seq, uint length, uint crc32)
    {
        var header = new FrameHeader(channelId, flags, seq, length, crc32);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        var read = FrameHeader.Read(buffer);
        
        Assert.Equal(channelId, read.ChannelId);
        Assert.Equal(flags, read.Flags);
        Assert.Equal(seq, read.Seq);
        Assert.Equal(length, read.Length);
        Assert.Equal(crc32, read.Crc32);
    }

    [Fact]
    public void FrameHeader_BigEndian_CorrectByteOrder()
    {
        var header = new FrameHeader(0x01020304, FrameFlags.Init, 0x11121314, 0x05060708, 0xAABBCCDD);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        
        // Channel ID bytes (big-endian) [0-3]
        Assert.Equal(0x01, buffer[0]);
        Assert.Equal(0x02, buffer[1]);
        Assert.Equal(0x03, buffer[2]);
        Assert.Equal(0x04, buffer[3]);
        
        // Flags [4]
        Assert.Equal((byte)FrameFlags.Init, buffer[4]);
        
        // Seq bytes (big-endian) [5-8]
        Assert.Equal(0x11, buffer[5]);
        Assert.Equal(0x12, buffer[6]);
        Assert.Equal(0x13, buffer[7]);
        Assert.Equal(0x14, buffer[8]);
        
        // Length bytes (big-endian) [9-12]
        Assert.Equal(0x05, buffer[9]);
        Assert.Equal(0x06, buffer[10]);
        Assert.Equal(0x07, buffer[11]);
        Assert.Equal(0x08, buffer[12]);
        
        // CRC32 bytes (big-endian) [13-16]
        Assert.Equal(0xAA, buffer[13]);
        Assert.Equal(0xBB, buffer[14]);
        Assert.Equal(0xCC, buffer[15]);
        Assert.Equal(0xDD, buffer[16]);
    }

    [Fact]
    public void FrameHeader_Size_Is17Bytes()
    {
        Assert.Equal(17, FrameHeader.Size);
    }
    
    [Fact]
    public void FrameHeader_CreateWithCrc_ComputesValidCrc()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var header = FrameHeader.CreateWithCrc(0x12345678, FrameFlags.Data, 42, payload);
        
        Assert.Equal(0x12345678u, header.ChannelId);
        Assert.Equal(FrameFlags.Data, header.Flags);
        Assert.Equal(42u, header.Seq);
        Assert.Equal(5u, header.Length);
        Assert.NotEqual(0u, header.Crc32);
        
        // Validate CRC
        Assert.True(header.ValidateCrc(payload));
    }
    
    [Fact]
    public void FrameHeader_ValidateCrc_ReturnsFalseForCorruptedPayload()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var header = FrameHeader.CreateWithCrc(0x12345678, FrameFlags.Data, 42, payload);
        
        // Corrupt the payload
        payload[2] = 0xFF;
        
        Assert.False(header.ValidateCrc(payload));
    }
    
    [Fact]
    public void FrameHeader_ComputeCrc32_IsDeterministic()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        
        var crc1 = FrameHeader.ComputeCrc32(0x12345678, FrameFlags.Data, 42, 5, payload);
        var crc2 = FrameHeader.ComputeCrc32(0x12345678, FrameFlags.Data, 42, 5, payload);
        
        Assert.Equal(crc1, crc2);
    }
    
    [Fact]
    public void FrameHeader_ComputeCrc32_DifferentForDifferentData()
    {
        var payload1 = new byte[] { 0x01, 0x02, 0x03 };
        var payload2 = new byte[] { 0x01, 0x02, 0x04 };
        
        var crc1 = FrameHeader.ComputeCrc32(0x12345678, FrameFlags.Data, 42, 3, payload1);
        var crc2 = FrameHeader.ComputeCrc32(0x12345678, FrameFlags.Data, 42, 3, payload2);
        
        Assert.NotEqual(crc1, crc2);
    }
}
