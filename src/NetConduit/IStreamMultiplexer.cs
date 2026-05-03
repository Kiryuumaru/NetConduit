namespace NetConduit;

/// <summary>
/// A transport-agnostic stream multiplexer that creates multiple virtual channels
/// over a single bidirectional stream.
/// </summary>
public interface IStreamMultiplexer : IAsyncDisposable
{
    /// <summary>The multiplexer configuration.</summary>
    MultiplexerOptions Options { get; }

    /// <summary>Session-level statistics.</summary>
    MultiplexerStats Stats { get; }

    /// <summary>Whether the transport is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Whether the multiplexer is running (started and not disposed).</summary>
    bool IsRunning { get; }

    /// <summary>Whether a graceful shutdown is in progress.</summary>
    bool IsShuttingDown { get; }

    /// <summary>The local session identity.</summary>
    Guid SessionId { get; }

    /// <summary>The remote peer's session identity.</summary>
    Guid RemoteSessionId { get; }

    /// <summary>String IDs of all currently active channels.</summary>
    IReadOnlyCollection<string> ActiveChannelIds { get; }

    /// <summary>Number of currently active channels.</summary>
    int ActiveChannelCount { get; }

    /// <summary>Reason for the last disconnection, if applicable.</summary>
    DisconnectReason? DisconnectReason { get; }

    /// <summary>Raised when a channel is opened.</summary>
    event Action<string>? OnChannelOpened;

    /// <summary>Raised when a channel is closed.</summary>
    event Action<string, Exception?>? OnChannelClosed;

    /// <summary>Raised when an error occurs.</summary>
    event Action<Exception>? OnError;

    /// <summary>Raised when the transport is disconnected.</summary>
    event Action<DisconnectReason, Exception?>? OnDisconnected;

    /// <summary>Raised when a reconnection attempt begins. Parameter is the attempt number.</summary>
    event Action<int>? OnReconnecting;

    /// <summary>Start the multiplexer (handshake, read/write loops).</summary>
    Task Start(CancellationToken ct = default);

    /// <summary>Wait until the multiplexer is ready to open/accept channels.</summary>
    Task WaitForReadyAsync(CancellationToken ct = default);

    /// <summary>Open a new outbound channel with the given ID.</summary>
    ValueTask<WriteChannel> OpenChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>Open a new outbound channel with full options.</summary>
    ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken ct = default);

    /// <summary>Accept an inbound channel with the given ID.</summary>
    ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>Accept all inbound channels as they arrive.</summary>
    IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken ct = default);

    /// <summary>Get a previously opened write channel by its ID.</summary>
    WriteChannel? GetWriteChannel(string channelId);

    /// <summary>Get a previously accepted read channel by its ID.</summary>
    ReadChannel? GetReadChannel(string channelId);

    /// <summary>Initiate graceful shutdown (GoAway).</summary>
    ValueTask GoAwayAsync(CancellationToken ct = default);

    /// <summary>Request an immediate flush of pending writes to the transport.</summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
