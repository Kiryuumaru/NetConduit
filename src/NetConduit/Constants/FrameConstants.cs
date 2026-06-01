namespace NetConduit.Constants;

internal static class FrameConstants
{
    internal const int HeaderSize = 12;
    internal const int MaxFramePayloadSize = 16 * 1024 * 1024; // 16 MB
    internal const int DefaultSlabSize = 1024 * 1024; // 1 MB per channel
    internal const int MinSlabSize = 64 * 1024; // 64 KB
    internal const int MaxSlabSize = 64 * 1024 * 1024; // 64 MB
}

internal static class ChannelConstants
{
    internal const uint ControlChannel = 0x00000000;
    internal const uint MinDataChannel = 0x00000001;
    internal const uint MaxDataChannel = 0xFFFFFFFE;
    internal const uint Reserved = 0xFFFFFFFF;
    internal const int MaxChannelIdLength = 1024;
}

internal static class CtrlSubtype
{
    internal const byte GoAway = 0x01;
    internal const byte Reconnect = 0x02;
    internal const byte ReconnectAck = 0x03;
}
