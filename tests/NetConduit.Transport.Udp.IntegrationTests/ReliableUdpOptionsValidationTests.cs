using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

public class ReliableUdpOptionsValidationTests
{
    [Fact]
    public void Mtu_AboveUdpDatagramPayloadLimit_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 65_542 });
    }

    [Fact]
    public void RetransmitTimeout_AboveCancellationTimerLimit_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { RetransmitTimeout = TimeSpan.FromDays(100) });
    }
}