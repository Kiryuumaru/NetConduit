using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

public class ReliableUdpOptionsValidationTests
{
    [Fact]
    public void Mtu_AtUdpDatagramPayloadLimit_IsAccepted()
    {
        var options = new ReliableUdpOptions { Mtu = 65_507 };

        Assert.Equal(65_507, options.Mtu);
    }

    [Fact]
    public void Mtu_AboveUdpDatagramPayloadLimit_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 65_542 });
    }

    [Fact]
    public void RetransmitTimeout_AtCancellationTimerLimit_IsAccepted()
    {
        var timeout = TimeSpan.FromMilliseconds(int.MaxValue);

        var options = new ReliableUdpOptions { RetransmitTimeout = timeout };

        Assert.Equal(timeout, options.RetransmitTimeout);
    }

    [Fact]
    public void RetransmitTimeout_AboveCancellationTimerLimit_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { RetransmitTimeout = TimeSpan.FromDays(100) });
    }

    [Fact]
    public void RetransmitTimeout_NegativeValue_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { RetransmitTimeout = TimeSpan.FromMilliseconds(-2) });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void MaxRetransmits_NegativeValue_Throws(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { MaxRetransmits = value });
        Assert.Equal(nameof(ReliableUdpOptions.MaxRetransmits), ex.ParamName);
    }

    [Fact]
    public void MaxRetransmits_Zero_IsAccepted()
    {
        var options = new ReliableUdpOptions { MaxRetransmits = 0 };

        Assert.Equal(0, options.MaxRetransmits);
    }

    [Fact]
    public void MaxRetransmits_Default_IsFive()
    {
        var options = new ReliableUdpOptions();

        Assert.Equal(5, options.MaxRetransmits);
    }
}