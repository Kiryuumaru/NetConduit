using NetConduit.Events;

namespace NetConduit.Interfaces;

/// <summary>
/// Base interface for all transits that wrap multiplexer channels.
/// Transits interpret raw bytes and add semantic meaning on top of channels.
/// </summary>
public interface ITransit : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the transit is ready (both channels confirmed by remote).
    /// Stays true forever once set.
    /// </summary>
    bool IsReady { get; }

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

    /// <summary>
    /// Raised once when both channels are confirmed ready. Never fires again.
    /// </summary>
    event EventHandler? Ready;

    /// <summary>
    /// Raised each time the underlying transport connects (including reconnects).
    /// </summary>
    event EventHandler? Connected;

    /// <summary>
    /// Raised each time the underlying transport disconnects.
    /// </summary>
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>
    /// Wait until the transit is confirmed ready (both channels acknowledged by remote).
    /// </summary>
    Task WaitForReadyAsync(CancellationToken ct = default);
}
