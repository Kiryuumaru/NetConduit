using System.Net.WebSockets;
using NetConduit;

namespace NetConduit.WebSocket;

/// <summary>
/// Represents a multiplexer connection over WebSocket, managing both the multiplexer and underlying WebSocket.
/// </summary>
public sealed class WebSocketMultiplexerConnection : IStreamMultiplexer, IDisposable
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly WebSocketStream _stream;
    private bool _disposed;

    internal WebSocketMultiplexerConnection(StreamMultiplexer multiplexer, System.Net.WebSockets.WebSocket webSocket, WebSocketStream stream)
    {
        _multiplexer = multiplexer;
        _webSocket = webSocket;
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

    /// <summary>
    /// The underlying WebSocket.
    /// </summary>
    public System.Net.WebSockets.WebSocket WebSocket => _webSocket;

    /// <summary>
    /// The current state of the WebSocket connection.
    /// </summary>
    public WebSocketState State => _webSocket.State;

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

    /// <summary>
    /// Closes the WebSocket connection gracefully.
    /// </summary>
    /// <param name="closeStatus">The close status.</param>
    /// <param name="statusDescription">The status description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CloseAsync(
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = null,
        CancellationToken cancellationToken = default)
    {
        await _multiplexer.GoAwayAsync(cancellationToken).ConfigureAwait(false);
        
        if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
        {
            await _webSocket.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        _stream.Dispose();
        _webSocket.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.Dispose();
        _webSocket.Dispose();
    }
}
