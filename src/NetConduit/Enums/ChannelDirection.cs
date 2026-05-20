namespace NetConduit.Enums;

/// <summary>
/// Direction of a channel registration. Outbound channels are opened locally
/// (the local side is the writer). Inbound channels are accepted locally
/// (the local side is the reader).
/// </summary>
public enum ChannelDirection
{
    /// <summary>Locally opened, write-only channel. Equivalent to <see cref="Interfaces.IStreamMultiplexer.OpenChannel"/>.</summary>
    Outbound,

    /// <summary>Locally accepted, read-only channel. Equivalent to <see cref="Interfaces.IStreamMultiplexer.AcceptChannel"/>.</summary>
    Inbound,
}
