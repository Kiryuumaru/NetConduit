using System.Net.Quic;
using System.Runtime.Versioning;
using NetConduit;

namespace NetConduit.Quic;

/// <summary>
/// Multiplexer connection over QUIC.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicMultiplexerConnection : IStreamMultiplexerConnection, IDisposable
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly QuicConnection _connection;
    private readonly QuicStream _stream;
    private bool _disposed;

    internal QuicMultiplexerConnection(StreamMultiplexer multiplexer, QuicConnection connection, QuicStream stream)
    {
        _multiplexer = multiplexer;
        _connection = connection;
        _stream = stream;
    }

    /// <summary>The multiplexer.</summary>
    public StreamMultiplexer Multiplexer => _multiplexer;

    /// <summary>Multiplexer options.</summary>
    public MultiplexerOptions Options => Multiplexer.Options;

    /// <summary>The underlying QUIC connection.</summary>
    public QuicConnection Connection => _connection;

    /// <summary>The underlying QUIC bidirectional stream.</summary>
    public QuicStream Stream => _stream;

    /// <summary>Runs the multiplexer loop.</summary>
    internal Task RunAsync(CancellationToken cancellationToken = default)
        => _multiplexer.RunAsync(cancellationToken);

    /// <summary>Starts the multiplexer and returns when it finishes.</summary>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => _multiplexer.StartAsync(cancellationToken);

    /// <summary>Opens an outbound channel with the given options.</summary>
    public ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
        => _multiplexer.OpenChannelAsync(options, cancellationToken);

    /// <summary>Accepts a specific inbound channel.</summary>
    public ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelAsync(channelId, cancellationToken);

    /// <summary>Enumerates inbound channels.</summary>
    public IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelsAsync(cancellationToken);

    /// <summary>Initiates graceful shutdown.</summary>
    public ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
        => _multiplexer.GoAwayAsync(cancellationToken);

    /// <summary>Current multiplexer statistics.</summary>
    public MultiplexerStats Stats => _multiplexer.Stats;

    /// <summary>
    /// Asynchronously disposes the multiplexer, stream, and QUIC connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Multiplexer.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronously disposes the multiplexer, stream, and QUIC connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
