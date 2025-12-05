using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NetConduit;

/// <summary>
/// Abstraction for a transport-agnostic stream multiplexer.
/// </summary>
public interface IStreamMultiplexer : IAsyncDisposable
{
    /// <summary>Multiplexer options.</summary>
    MultiplexerOptions Options { get; }

    /// <summary>Current multiplexer statistics.</summary>
    MultiplexerStats Stats { get; }

    /// <summary>Whether the multiplexer is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Whether the multiplexer run loop is active.</summary>
    bool IsRunning { get; }

    /// <summary>Whether a GOAWAY has been sent or received (graceful shutdown in progress).</summary>
    bool IsShuttingDown { get; }

    /// <summary>The session identifier for this multiplexer.</summary>
    Guid SessionId { get; }

    /// <summary>The remote session identifier (available after handshake).</summary>
    Guid RemoteSessionId { get; }

    /// <summary>Gets the IDs of all active channels (both opened and accepted).</summary>
    IReadOnlyCollection<string> ActiveChannelIds { get; }

    /// <summary>Gets the IDs of channels opened by this side.</summary>
    IReadOnlyCollection<string> OpenedChannelIds { get; }

    /// <summary>Gets the IDs of channels accepted from the remote side.</summary>
    IReadOnlyCollection<string> AcceptedChannelIds { get; }

    /// <summary>Gets the count of active channels.</summary>
    int ActiveChannelCount { get; }

    /// <summary>Event raised when a channel is opened.</summary>
    event Action<string>? OnChannelOpened;

    /// <summary>Event raised when a channel is closed.</summary>
    event Action<string, Exception?>? OnChannelClosed;

    /// <summary>Event raised when an error occurs.</summary>
    event Action<Exception>? OnError;

    /// <summary>Starts the multiplexer and returns when the processing loop exits.</summary>
    Task<Task> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens an outbound channel.</summary>
    ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default);

    /// <summary>Accepts a specific inbound channel.</summary>
    ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Enumerates inbound channels as they arrive.</summary>
    IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a write channel by its ChannelId.</summary>
    WriteChannel? GetWriteChannel(string channelId);

    /// <summary>Gets a read channel by its ChannelId.</summary>
    ReadChannel? GetReadChannel(string channelId);

    /// <summary>Initiates a graceful shutdown (no new channels).</summary>
    ValueTask GoAwayAsync(CancellationToken cancellationToken = default);
}
