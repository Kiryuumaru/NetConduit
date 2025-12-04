using System.Net.WebSockets;

namespace NetConduit.WebSocket;

/// <summary>
/// WebSocket transport helper for creating multiplexers over WebSocket connections.
/// </summary>
public static class WebSocketMultiplexer
{
    /// <summary>
    /// Connects to a WebSocket server and creates a multiplexer.
    /// </summary>
    /// <param name="uri">The WebSocket URI to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="clientOptions">Optional WebSocket client options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying WebSocket.</returns>
    public static async Task<WebSocketMultiplexerConnection> ConnectAsync(
        Uri uri,
        MultiplexerOptions? options = null,
        Action<ClientWebSocketOptions>? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var webSocket = new ClientWebSocket();
        try
        {
            clientOptions?.Invoke(webSocket.Options);
            await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            var stream = new WebSocketStream(webSocket);
            var multiplexer = new StreamMultiplexer(stream, stream, options);
            return new WebSocketMultiplexerConnection(multiplexer, webSocket, stream);
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects to a WebSocket server and creates a multiplexer.
    /// </summary>
    /// <param name="url">The WebSocket URL to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="clientOptions">Optional WebSocket client options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying WebSocket.</returns>
    public static Task<WebSocketMultiplexerConnection> ConnectAsync(
        string url,
        MultiplexerOptions? options = null,
        Action<ClientWebSocketOptions>? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        return ConnectAsync(new Uri(url), options, clientOptions, cancellationToken);
    }

    /// <summary>
    /// Creates a multiplexer from an existing WebSocket (server-side accept).
    /// </summary>
    /// <param name="webSocket">The WebSocket connection.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <returns>A multiplexer wrapping the WebSocket.</returns>
    public static WebSocketMultiplexerConnection Accept(
        System.Net.WebSockets.WebSocket webSocket,
        MultiplexerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        var stream = new WebSocketStream(webSocket);
        var multiplexer = new StreamMultiplexer(stream, stream, options);
        return new WebSocketMultiplexerConnection(multiplexer, webSocket, stream);
    }
}
