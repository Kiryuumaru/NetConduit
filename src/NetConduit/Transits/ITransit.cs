namespace NetConduit.Transits;

/// <summary>
/// Base interface for all transits that wrap multiplexer channels.
/// Transits interpret raw bytes and add semantic meaning on top of channels.
/// </summary>
public interface ITransit : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the transit is connected and operational.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the channel ID for the write channel (outgoing data), if any.
    /// </summary>
    string? WriteChannelId { get; }

    /// <summary>
    /// Gets the channel ID for the read channel (incoming data), if any.
    /// </summary>
    string? ReadChannelId { get; }
}
