namespace NetConduit.Udp;

/// <summary>
/// Options for the reliable UDP stream shim.
/// </summary>
public sealed class ReliableUdpOptions
{
    private const int HeaderSize = 7;

    /// <summary>
    /// Maximum datagram size including header. Default: 1200 bytes.
    /// Valid range: 8 (7-byte header + 1 byte payload) to 65542.
    /// </summary>
    public int Mtu
    {
        get => _mtu;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, HeaderSize + 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ushort.MaxValue + HeaderSize);
            _mtu = value;
        }
    }

    private readonly int _mtu = 1200;

    /// <summary>Timeout before retransmitting an unacknowledged packet. Default: 1 second.</summary>
    public TimeSpan RetransmitTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum retransmission attempts before failing the write. Default: 5.</summary>
    public int MaxRetransmits { get; init; } = 5;
}
