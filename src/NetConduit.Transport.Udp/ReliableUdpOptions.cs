namespace NetConduit.Transport.Udp;

/// <summary>
/// Options for the reliable UDP stream shim.
/// </summary>
public sealed class ReliableUdpOptions
{
    private const int HeaderSize = 7;
    private const int MinMtu = HeaderSize + 1;
    private const int MaxUdpDatagramSize = 65_507;
    private const int MaxCancellationTimerDelayMilliseconds = int.MaxValue;

    /// <summary>
    /// Maximum datagram size including header. Default: 1200 bytes.
    /// Valid range: 8 (7-byte header + 1 byte payload) to 65,507.
    /// </summary>
    public int Mtu
    {
        get => _mtu;
        init
        {
            if (value is < MinMtu or > MaxUdpDatagramSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Mtu),
                    value,
                    $"Mtu must be between {MinMtu} and {MaxUdpDatagramSize} bytes inclusive.");
            }

            _mtu = value;
        }
    }

    private readonly int _mtu = 1200;

    /// <summary>Timeout before retransmitting an unacknowledged packet. Default: 1 second.</summary>
    public TimeSpan RetransmitTimeout
    {
        get => _retransmitTimeout;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RetransmitTimeout),
                    value,
                    "RetransmitTimeout must be non-negative.");
            }

            if (value.TotalMilliseconds > MaxCancellationTimerDelayMilliseconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RetransmitTimeout),
                    value,
                    $"RetransmitTimeout must not exceed {int.MaxValue} milliseconds (approximately 24.86 days).");
            }

            _retransmitTimeout = value;
        }
    }

    private readonly TimeSpan _retransmitTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Maximum retransmission attempts before failing the write. Default: 5.</summary>
    public int MaxRetransmits { get; init; } = 5;
}
