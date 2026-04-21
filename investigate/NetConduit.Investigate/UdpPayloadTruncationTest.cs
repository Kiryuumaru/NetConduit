namespace NetConduit.Investigate;

/// <summary>
/// Proves that ReliableUdpStream truncates payload length to UInt16,
/// causing silent data loss when MTU exceeds 65542.
/// </summary>
public class UdpPayloadTruncationTest
{
    [Fact]
    public void UshortCast_TruncatesLargePayloadLength()
    {
        // Reproduce the exact code from SendPacketAsync:
        //   BinaryPrimitives.WriteUInt16BigEndian(header[5..7], (ushort)payload.Length);

        int payloadLength = 69993; // From MTU=70000, maxPayload = 70000 - 7 = 69993

        ushort encoded = (ushort)payloadLength;

        // ushort max = 65535. 69993 mod 65536 = 4457
        Assert.Equal(4457, (int)encoded);
        Assert.NotEqual(payloadLength, (int)encoded);

        // This means the receiver will read only 4457 bytes of a 69993-byte packet.
        // 65,536 bytes are SILENTLY DROPPED.
    }

    [Theory]
    [InlineData(1400, 1393)]    // Default MTU — safe
    [InlineData(1500, 1493)]    // Standard Ethernet — safe
    [InlineData(9000, 8993)]    // Jumbo frame — safe
    [InlineData(65542, 65535)]  // Max safe: exactly ushort.MaxValue payload
    [InlineData(65543, 0)]      // One over: payload=65536, truncates to 0!
    [InlineData(70000, 4457)]   // Realistic jumbo: massive truncation
    [InlineData(131078, 65535)] // Double: wraps back to 65535
    public void MtuToPayloadLength_TruncationMatrix(int mtu, int expectedEncodedLength)
    {
        int maxPayload = Math.Max(1, mtu - 7);
        ushort encoded = (ushort)maxPayload;

        Assert.Equal(expectedEncodedLength, (int)encoded);

        if (mtu > 65542)
        {
            Assert.NotEqual(maxPayload, (int)encoded);
        }
    }

    [Fact]
    public void MtuOf65543_TruncatesPayloadTo_Zero()
    {
        // The worst case: MTU = 65543 → maxPayload = 65536 → (ushort)65536 = 0
        int mtu = 65543;
        int maxPayload = Math.Max(1, mtu - 7); // 65536
        ushort encoded = (ushort)maxPayload;    // 0

        Assert.Equal(65536, maxPayload);
        Assert.Equal(0, (int)encoded);

        // The receiver reads len=0 and copies zero bytes from the packet.
        // The entire payload is silently lost.
    }

    [Fact]
    public void NoMtuValidation_AllowsAnyValue()
    {
        // ReliableUdpOptions has no validation on MTU
        var options = new Udp.ReliableUdpOptions
        {
            Mtu = 0
        };
        Assert.Equal(0, options.Mtu);

        var options2 = new Udp.ReliableUdpOptions
        {
            Mtu = -1
        };
        Assert.Equal(-1, options2.Mtu);

        var options3 = new Udp.ReliableUdpOptions
        {
            Mtu = int.MaxValue
        };
        Assert.Equal(int.MaxValue, options3.Mtu);
    }

    [Fact]
    public void MtuZero_CausesMaxPayloadOfOne()
    {
        // Math.Max(1, 0 - 7) = Math.Max(1, -7) = 1
        int mtu = 0;
        int maxPayload = Math.Max(1, mtu - 7);
        Assert.Equal(1, maxPayload);
        // This means each write sends 1 byte at a time + 7 header bytes = 8 bytes per packet
        // Extremely inefficient but technically "works"
    }

    [Fact]
    public void NegativeMtu_StillProducesValidMaxPayload()
    {
        // Math.Max(1, -100 - 7) = Math.Max(1, -107) = 1
        int mtu = -100;
        int maxPayload = Math.Max(1, mtu - 7);
        Assert.Equal(1, maxPayload);
        // Negative MTU silently degrades to 1-byte payloads
    }
}
