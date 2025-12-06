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
public sealed class QuicMultiplexerConnection : IStreamMultiplexer, IDisposable
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

    /// <summary>Multiplexer options.</summary>
    public MultiplexerOptions Options => _multiplexer.Options;

    /// <summary>Multiplexer statistics.</summary>
    public MultiplexerStats Stats => _multiplexer.Stats;

    /// <summary>Whether the multiplexer is currently connected.</summary>
    public bool IsConnected => _multiplexer.IsConnected;

    /// <summary>Whether the multiplexer run loop is active.</summary>
    public bool IsRunning => _multiplexer.IsRunning;

    /// <summary>Whether a GOAWAY has been sent or received.</summary>
    public bool IsShuttingDown => _multiplexer.IsShuttingDown;

    /// <summary>The session identifier for this multiplexer.</summary>
    public Guid SessionId => _multiplexer.SessionId;

    /// <summary>The remote session identifier.</summary>
    public Guid RemoteSessionId => _multiplexer.RemoteSessionId;

    /// <summary>Gets the IDs of all active channels.</summary>
    public IReadOnlyCollection<string> ActiveChannelIds => _multiplexer.ActiveChannelIds;

    /// <summary>Gets the IDs of channels opened by this side.</summary>
    public IReadOnlyCollection<string> OpenedChannelIds => _multiplexer.OpenedChannelIds;

    /// <summary>Gets the IDs of channels accepted from the remote side.</summary>
    public IReadOnlyCollection<string> AcceptedChannelIds => _multiplexer.AcceptedChannelIds;

    /// <summary>Gets the count of active channels.</summary>
    public int ActiveChannelCount => _multiplexer.ActiveChannelCount;

    /// <summary>Event raised when a channel is opened.</summary>
    public event Action<string>? OnChannelOpened
    {
        add => _multiplexer.OnChannelOpened += value;
        remove => _multiplexer.OnChannelOpened -= value;
    }

    /// <summary>Event raised when a channel is closed.</summary>
    public event Action<string, Exception?>? OnChannelClosed
    {
        add => _multiplexer.OnChannelClosed += value;
        remove => _multiplexer.OnChannelClosed -= value;
    }

    /// <summary>Event raised when an error occurs.</summary>
    public event Action<Exception>? OnError
    {
        add => _multiplexer.OnError += value;
        remove => _multiplexer.OnError -= value;
    }

    /// <summary>Event raised when the multiplexer disconnects.</summary>
    public event Action<DisconnectReason, Exception?>? OnDisconnected
    {
        add => _multiplexer.OnDisconnected += value;
        remove => _multiplexer.OnDisconnected -= value;
    }

    /// <summary>The reason for disconnection, if disconnected.</summary>
    public DisconnectReason? DisconnectReason => _multiplexer.DisconnectReason;

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

    /// <summary>Gets a write channel by its ChannelId.</summary>
    public WriteChannel? GetWriteChannel(string channelId)
        => _multiplexer.GetWriteChannel(channelId);

    /// <summary>Gets a read channel by its ChannelId.</summary>
    public ReadChannel? GetReadChannel(string channelId)
        => _multiplexer.GetReadChannel(channelId);

    /// <summary>
    /// Asynchronously disposes the multiplexer, stream, and QUIC connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
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
        _multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
