using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NetConduit.Tcp;

/// <summary>
/// Represents a multiplexer connection over TCP, managing both the multiplexer and underlying TCP client.
/// </summary>
public sealed class TcpMultiplexerConnection : IStreamMultiplexer, IDisposable
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly TcpClient _client;
    private bool _disposed;

    internal TcpMultiplexerConnection(StreamMultiplexer multiplexer, TcpClient client)
    {
        _multiplexer = multiplexer;
        _client = client;
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

    /// <summary>
    /// The underlying TCP client.
    /// </summary>
    public TcpClient Client => _client;

    /// <summary>
    /// Whether the TCP connection is connected.
    /// </summary>
    public bool Connected => _client.Connected;

    /// <summary>
    /// The local endpoint of the TCP connection.
    /// </summary>
    public System.Net.EndPoint? LocalEndPoint => _client.Client?.LocalEndPoint;

    /// <summary>
    /// The remote endpoint of the TCP connection.
    /// </summary>
    public System.Net.EndPoint? RemoteEndPoint => _client.Client?.RemoteEndPoint;

    /// <summary>
    /// Starts the multiplexer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task RunAsync(CancellationToken cancellationToken = default)
        => _multiplexer.RunAsync(cancellationToken);

    /// <summary>
    /// Starts the multiplexer and waits for handshake to complete.
    /// After this method returns, the multiplexer is ready to open and accept channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the background processing. This task completes when the multiplexer shuts down.</returns>
    /// <example>
    /// <code>
    /// var connection = await TcpMultiplexer.ConnectAsync("localhost", 5000);
    /// var runTask = await connection.StartAsync(cancellationToken);
    /// 
    /// // Connection is ready - open channels
    /// var channel = await connection.OpenChannelAsync(new ChannelOptions { ChannelId = "data" });
    /// </code>
    /// </example>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => _multiplexer.StartAsync(cancellationToken);

    /// <summary>
    /// Opens a new channel for writing data.
    /// </summary>
    /// <param name="options">The channel options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write channel for sending data.</returns>
    public ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
        => _multiplexer.OpenChannelAsync(options, cancellationToken);

    /// <summary>
    /// Accepts a specific channel by ID.
    /// </summary>
    /// <param name="channelId">The channel ID to accept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read channel for receiving data.</returns>
    public ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelAsync(channelId, cancellationToken);

    /// <summary>
    /// Accepts incoming channels as they arrive.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of read channels.</returns>
    public IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelsAsync(cancellationToken);

    /// <summary>
    /// Initiates graceful shutdown of the multiplexer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
        => _multiplexer.GoAwayAsync(cancellationToken);

    /// <summary>Gets a write channel by its ChannelId.</summary>
    public WriteChannel? GetWriteChannel(string channelId)
        => _multiplexer.GetWriteChannel(channelId);

    /// <summary>Gets a read channel by its ChannelId.</summary>
    public ReadChannel? GetReadChannel(string channelId)
        => _multiplexer.GetReadChannel(channelId);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
    }
}
