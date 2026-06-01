using NetConduit.Internal;
using NetConduit.Exceptions;

namespace NetConduit.UnitTests;

public sealed class FrameHeaderTests
{
    [Fact]
    public void RoundTrip_DataFrame()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 42, flags: FrameFlags.Data, payloadLength: 1024);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(42u, header.ChannelIndex);
        Assert.Equal(FrameFlags.Data, header.Flags);
        Assert.Equal(1024, header.PayloadLength);
    }

    [Fact]
    public void RoundTrip_MaxChannel()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0xFFFFFFFE, flags: FrameFlags.Init, payloadLength: 0);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(0xFFFFFFFEu, header.ChannelIndex);
        Assert.Equal(FrameFlags.Init, header.Flags);
        Assert.Equal(0, header.PayloadLength);
    }

    [Fact]
    public void RoundTrip_ControlChannel()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0x0000, flags: FrameFlags.Ping, payloadLength: 8);

        var header = FrameHeader.Parse(buf);

        Assert.Equal(0u, header.ChannelIndex);
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
        FrameHeader.WriteTo(buf, channelIndex: 0x01020304, flags: FrameFlags.Data, payloadLength: 0x05060708);

        // Channel: 0x01, 0x02, 0x03, 0x04 (big-endian)
        Assert.Equal(0x01, buf[0]);
        Assert.Equal(0x02, buf[1]);
        Assert.Equal(0x03, buf[2]);
        Assert.Equal(0x04, buf[3]);
        // Flags: Data = 0x00
        Assert.Equal(0x00, buf[4]);
        // Reserved bytes
        Assert.Equal(0x00, buf[5]);
        Assert.Equal(0x00, buf[6]);
        Assert.Equal(0x00, buf[7]);
        // Payload length: 0x05, 0x06, 0x07, 0x08 (big-endian)
        Assert.Equal(0x05, buf[8]);
        Assert.Equal(0x06, buf[9]);
        Assert.Equal(0x07, buf[10]);
        Assert.Equal(0x08, buf[11]);
    }

    [Fact]
    public void HeaderSize_IsTwelve()
    {
        Assert.Equal(12, FrameHeader.Size);
    }

    [Fact]
    public void Parse_UnknownFrameFlag_ThrowsProtocolError()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 1, flags: (FrameFlags)0xFF, payloadLength: 0);
        byte[] header = buf.ToArray();

        var ex = Assert.Throws<MultiplexerException>(() => FrameHeader.Parse(header));

        Assert.Equal(ErrorCode.ProtocolError, ex.ErrorCode);
    }

    [Fact]
    public void Parse_NonZeroReservedHeaderByte_ThrowsProtocolError()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0, flags: FrameFlags.Ping, payloadLength: 8);
        buf[5] = 0xAA;
        byte[] header = buf.ToArray();

        var ex = Assert.Throws<MultiplexerException>(() => FrameHeader.Parse(header));

        Assert.Equal(ErrorCode.ProtocolError, ex.ErrorCode);
    }

    [Fact]
    public void Parse_ReservedChannelIndex_ThrowsProtocolError()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        FrameHeader.WriteTo(buf, channelIndex: 0xFFFFFFFF, flags: FrameFlags.Init, payloadLength: 1);
        byte[] header = buf.ToArray();

        var ex = Assert.Throws<MultiplexerException>(() => FrameHeader.Parse(header));

        Assert.Equal(ErrorCode.ProtocolError, ex.ErrorCode);
    }
}
