using System.Net.WebSockets;

namespace NetConduit.WebSocket;

/// <summary>
/// Extension methods for creating multiplexers from WebSocket types.
/// </summary>
public static class WebSocketMultiplexerExtensions
{
    /// <summary>
    /// Creates a multiplexer from an existing WebSocket connection.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <returns>A multiplexer wrapping the WebSocket.</returns>
    /// <example>
    /// <code>
    /// // Server-side (ASP.NET Core)
    /// var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    /// var connection = webSocket.AsMux();
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static WebSocketMultiplexerConnection AsMux(
        this System.Net.WebSockets.WebSocket webSocket,
        MultiplexerOptions? options = null)
    {
        return WebSocketMultiplexer.Accept(webSocket, options);
    }

    /// <summary>
    /// Connects the ClientWebSocket and creates a multiplexer.
    /// </summary>
    /// <param name="webSocket">The ClientWebSocket (will be connected).</param>
    /// <param name="uri">The WebSocket URI to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying WebSocket.</returns>
    /// <example>
    /// <code>
    /// var webSocket = new ClientWebSocket();
    /// var connection = await webSocket.ConnectMuxAsync(new Uri("ws://localhost:5000/ws"));
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static async Task<WebSocketMultiplexerConnection> ConnectMuxAsync(
        this ClientWebSocket webSocket,
        Uri uri,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        var stream = new WebSocketStream(webSocket);
        var multiplexer = new StreamMultiplexer(stream, stream, options);
        return new WebSocketMultiplexerConnection(multiplexer, webSocket, stream);
    }

    /// <summary>
    /// Connects the ClientWebSocket and creates a multiplexer.
    /// </summary>
    /// <param name="webSocket">The ClientWebSocket (will be connected).</param>
    /// <param name="url">The WebSocket URL to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying WebSocket.</returns>
    /// <example>
    /// <code>
    /// var webSocket = new ClientWebSocket();
    /// var connection = await webSocket.ConnectMuxAsync("ws://localhost:5000/ws");
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static Task<WebSocketMultiplexerConnection> ConnectMuxAsync(
        this ClientWebSocket webSocket,
        string url,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ConnectMuxAsync(webSocket, new Uri(url), options, cancellationToken);
    }
}
