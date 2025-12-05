namespace NetConduit.Udp;

/// <summary>
/// Options for the minimal reliable UDP stream shim.
/// </summary>
public sealed class ReliableUdpOptions
{
    /// <summary>Maximum datagram size (including header). Default: 1200 bytes.</summary>
    public int Mtu { get; init; } = 1200;

    /// <summary>Timeout before retransmitting an unacknowledged packet. Default: 1 second.</summary>
    public TimeSpan RetransmitTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum retransmission attempts before failing the write. Default: 5.</summary>
    public int MaxRetransmits { get; init; } = 5;
}
