using NetConduit.Udp;

namespace NetConduit.Udp.IntegrationTests;

public class ReliableUdpOptionsBugFixTests
{
    [Fact]
    public void ReliableUdpOptions_MtuTooLarge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 70000 });
    }

    [Fact]
    public void ReliableUdpOptions_MtuZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 0 });
    }

    [Fact]
    public void ReliableUdpOptions_MtuNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = -1 });
    }

    [Fact]
    public void ReliableUdpOptions_MtuBelowMinimum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 7 });
    }

    [Fact]
    public void ReliableUdpOptions_MtuAtMaxUshortPayload_Accepted()
    {
        var opts = new ReliableUdpOptions { Mtu = 65542 };
        Assert.Equal(65542, opts.Mtu);
    }

    [Fact]
    public void ReliableUdpOptions_MtuAboveMaxUshortPayload_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReliableUdpOptions { Mtu = 65543 });
    }

    [Fact]
    public void ReliableUdpOptions_MtuDefault_Is1200()
    {
        var opts = new ReliableUdpOptions();
        Assert.Equal(1200, opts.Mtu);
    }

    [Fact]
    public void ReliableUdpOptions_MinimumValidMtu_Accepted()
    {
        var opts = new ReliableUdpOptions { Mtu = 8 };
        Assert.Equal(8, opts.Mtu);
    }
}
