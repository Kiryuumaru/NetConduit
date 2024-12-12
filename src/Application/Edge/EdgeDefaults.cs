namespace Application.Edge;

public static class EdgeDefaults
{
    public const int EdgeKeySize = 64;

    public const int EdgeCommsBufferSize = 16384;

    public const int EdgeHandshakeRequestLength = 4096;

    public const int EdgeHandshakeRSABitsLength = 4096;

    public static readonly Guid HandshakeChannel = new("00000000-0000-0000-0000-000000000001");

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan RelayedApiTimeout = TimeSpan.FromSeconds(10);

    public static readonly int RawMockChannelCount = 500;
    public static readonly int MsgMockChannelCount = 100;

    public static readonly int MockChannelKeyOffset = 1000;
    public static readonly int MockAveCount = 10;
}
