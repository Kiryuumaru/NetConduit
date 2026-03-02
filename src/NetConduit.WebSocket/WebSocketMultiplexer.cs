using System.Net.WebSockets;

namespace NetConduit.WebSocket;

/// <summary>
/// WebSocket transport helper for creating multiplexers over WebSocket connections.
/// </summary>
public static class WebSocketMultiplexer
{
    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified WebSocket URI.
    /// Supports reconnection - each call to StreamFactory creates a new WebSocket connection.
    /// </summary>
    /// <param name="uri">The WebSocket URI to connect to.</param>
    /// <param name="clientOptions">Optional action to configure WebSocket client options.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for WebSocket client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        Uri uri,
        Action<ClientWebSocketOptions>? clientOptions = null,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var webSocket = new ClientWebSocket();
                clientOptions?.Invoke(webSocket.Options);
                await webSocket.ConnectAsync(uri, ct).ConfigureAwait(false);
                var stream = new WebSocketStream(webSocket);
                return new StreamPair(stream, webSocket);
            }
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified WebSocket URL.
    /// Supports reconnection - each call to StreamFactory creates a new WebSocket connection.
    /// </summary>
    /// <param name="url">The WebSocket URL to connect to.</param>
    /// <param name="clientOptions">Optional action to configure WebSocket client options.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for WebSocket client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string url,
        Action<ClientWebSocketOptions>? clientOptions = null,
        Action<MultiplexerOptions>? configure = null)
    {
        return CreateOptions(new Uri(url), clientOptions, configure);
    }

    /// <summary>
    /// Creates multiplexer options for an already-accepted WebSocket connection (server-side).
    /// Reconnection is disabled by default for server-side connections.
    /// </summary>
    /// <param name="webSocket">The accepted WebSocket connection.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for the accepted WebSocket.</returns>
    public static MultiplexerOptions CreateServerOptions(
        System.Net.WebSockets.WebSocket webSocket,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = ct =>
            {
                if (accepted)
                {
                    throw new InvalidOperationException(
                        "Server-side WebSocket multiplexer does not support reconnection. " +
                        "Accept a new WebSocket connection to create another multiplexer.");
                }

                accepted = true;
                var stream = new WebSocketStream(webSocket);
                return Task.FromResult<IStreamPair>(new StreamPair(stream, webSocket));
            }
        };

        configure?.Invoke(options);
        return options;
    }
}
