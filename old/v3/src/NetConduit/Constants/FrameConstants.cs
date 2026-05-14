namespace NetConduit.Constants;

internal static class FrameConstants
{
    internal const int HeaderSize = 8;
    internal const int MaxFramePayloadSize = 16 * 1024 * 1024; // 16 MB
    internal const int DefaultSlabSize = 1024 * 1024; // 1 MB per channel
    internal const int MinSlabSize = 64 * 1024; // 64 KB
    internal const int MaxSlabSize = 64 * 1024 * 1024; // 64 MB
}

internal static class ChannelConstants
{
    internal const ushort ControlChannel = 0x0000;
    internal const ushort MinDataChannel = 0x0001;
    internal const ushort MaxDataChannel = 0xFFFE;
    internal const ushort Reserved = 0xFFFF;
    internal const int MaxChannelIdLength = 1024;
}

internal static class CtrlSubtype
{
    internal const byte GoAway = 0x01;
    internal const byte Reconnect = 0x02;
    internal const byte ReconnectAck = 0x03;
}
