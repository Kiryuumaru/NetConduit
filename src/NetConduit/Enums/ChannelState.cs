namespace NetConduit;

/// <summary>
/// Channel state.
/// </summary>
public enum ChannelState : byte
{
    /// <summary>Channel is being opened, waiting for ACK.</summary>
    Opening,
    
    /// <summary>Channel is open and ready for data transfer.</summary>
    Open,
    
    /// <summary>Channel is closing, FIN sent or received.</summary>
    Closing,
    
    /// <summary>Channel is closed.</summary>
    Closed
}
