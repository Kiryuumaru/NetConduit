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

    /// <summary>Starts the multiplexer and returns when the processing loop exits.</summary>
    Task<Task> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens an outbound channel.</summary>
    ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default);

    /// <summary>Accepts a specific inbound channel.</summary>
    ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Enumerates inbound channels as they arrive.</summary>
    IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Initiates a graceful shutdown (no new channels).</summary>
    ValueTask GoAwayAsync(CancellationToken cancellationToken = default);
}
