namespace NetConduit.Enums;

/// <summary>
/// Lifecycle state of a channel.
/// </summary>
public enum ChannelState : byte
{
    /// <summary>INIT frame sent, awaiting confirmation.</summary>
    Opening,
    /// <summary>Channel is active and ready for data.</summary>
    Open,
    /// <summary>FIN frame sent, draining remaining data.</summary>
    Closing,
    /// <summary>Channel is fully closed.</summary>
    Closed,
}
