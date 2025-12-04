using System.Net.WebSockets;

namespace NetConduit.WebSocket;

/// <summary>
/// Represents a multiplexer connection over WebSocket, managing both the multiplexer and underlying WebSocket.
/// </summary>
public sealed class WebSocketMultiplexerConnection : IAsyncDisposable, IDisposable
{
    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly WebSocketStream _stream;
    private bool _disposed;

    internal WebSocketMultiplexerConnection(StreamMultiplexer multiplexer, System.Net.WebSockets.WebSocket webSocket, WebSocketStream stream)
    {
        Multiplexer = multiplexer;
        _webSocket = webSocket;
        _stream = stream;
    }

    /// <summary>
    /// The stream multiplexer.
    /// </summary>
    public StreamMultiplexer Multiplexer { get; }

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
        => Multiplexer.RunAsync(cancellationToken);

    /// <summary>
    /// Starts the multiplexer and waits for handshake to complete.
    /// After this method returns, the multiplexer is ready to open and accept channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the background processing. This task completes when the multiplexer shuts down.</returns>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => Multiplexer.StartAsync(cancellationToken);

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
        await Multiplexer.GoAwayAsync(cancellationToken).ConfigureAwait(false);
        
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

        await Multiplexer.DisposeAsync().ConfigureAwait(false);
        _stream.Dispose();
        _webSocket.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.Dispose();
        _webSocket.Dispose();
    }
}
