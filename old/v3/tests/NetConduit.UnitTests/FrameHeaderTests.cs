using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class FrameHeaderTests
{
    [Fact]
    public void RoundTrip_DataFrame()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 42, flags: FrameFlags.Data, payloadLength: 1024);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(42, header.ChannelIndex);
        Assert.Equal(FrameFlags.Data, header.Flags);
        Assert.Equal(1024, header.PayloadLength);
    }

    [Fact]
    public void RoundTrip_MaxChannel()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0xFFFE, flags: FrameFlags.Init, payloadLength: 0);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(0xFFFE, header.ChannelIndex);
        Assert.Equal(FrameFlags.Init, header.Flags);
        Assert.Equal(0, header.PayloadLength);
    }

    [Fact]
    public void RoundTrip_ControlChannel()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0x0000, flags: FrameFlags.Ping, payloadLength: 8);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(0x0000, header.ChannelIndex);
        Assert.Equal(FrameFlags.Ping, header.Flags);
        Assert.Equal(8, header.PayloadLength);
    }

    [Fact]
    public void RoundTrip_AllFrameTypes()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];

        foreach (FrameFlags flag in Enum.GetValues<FrameFlags>())
        {
            FrameHeader.WriteTo(buf, channelIndex: 1, flags: flag, payloadLength: 100);
            var header = FrameHeader.Parse(buf);
            Assert.Equal(flag, header.Flags);
        }
    }

    [Fact]
    public void BigEndian_ByteOrder()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0x0102, flags: FrameFlags.Data, payloadLength: 0x03040506);

        // Channel: 0x01, 0x02 (big-endian)
        Assert.Equal(0x01, buf[0]);
        Assert.Equal(0x02, buf[1]);
        // Flags: Data = 0x00
        Assert.Equal(0x00, buf[2]);
        // Reserved
        Assert.Equal(0x00, buf[3]);
        // Payload length: 0x03, 0x04, 0x05, 0x06 (big-endian)
        Assert.Equal(0x03, buf[4]);
        Assert.Equal(0x04, buf[5]);
        Assert.Equal(0x05, buf[6]);
        Assert.Equal(0x06, buf[7]);
    }

    [Fact]
    public void HeaderSize_IsEight()
    {
        Assert.Equal(8, FrameHeader.Size);
    }
}
