using System.Buffers.Binary;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

public class FrameHeaderTests
{
    [Fact]
    public void FrameHeader_RoundTrip_PreservesValues()
    {
        var header = new FrameHeader(0x12345678, FrameFlags.Data, 0xABCDEF01);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        var read = FrameHeader.Read(buffer);
        
        Assert.Equal(header.ChannelId, read.ChannelId);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.Length, read.Length);
    }

    [Theory]
    [InlineData(0u, FrameFlags.Data, 0u)]
    [InlineData(uint.MaxValue - 1, FrameFlags.Init, uint.MaxValue)]
    [InlineData(0x80000000, FrameFlags.Fin, 1024)]
    [InlineData(1, FrameFlags.Ack, 65536)]
    [InlineData(0x7FFFFFFF, FrameFlags.Err, 16 * 1024 * 1024)]
    public void FrameHeader_RoundTrip_AllFlags(uint channelId, FrameFlags flags, uint length)
    {
        var header = new FrameHeader(channelId, flags, length);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        var read = FrameHeader.Read(buffer);
        
        Assert.Equal(channelId, read.ChannelId);
        Assert.Equal(flags, read.Flags);
        Assert.Equal(length, read.Length);
    }

    [Fact]
    public void FrameHeader_BigEndian_CorrectByteOrder()
    {
        var header = new FrameHeader(0x01020304, FrameFlags.Init, 0x05060708);
        var buffer = new byte[FrameHeader.Size];
        
        header.Write(buffer);
        
        // Channel ID bytes (big-endian)
        Assert.Equal(0x01, buffer[0]);
        Assert.Equal(0x02, buffer[1]);
        Assert.Equal(0x03, buffer[2]);
        Assert.Equal(0x04, buffer[3]);
        
        // Flags
        Assert.Equal((byte)FrameFlags.Init, buffer[4]);
        
        // Length bytes (big-endian)
        Assert.Equal(0x05, buffer[5]);
        Assert.Equal(0x06, buffer[6]);
        Assert.Equal(0x07, buffer[7]);
        Assert.Equal(0x08, buffer[8]);
    }

    [Fact]
    public void FrameHeader_Size_Is9Bytes()
    {
        Assert.Equal(9, FrameHeader.Size);
    }
}
