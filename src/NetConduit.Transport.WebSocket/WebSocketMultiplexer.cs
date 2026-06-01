using System.Net.WebSockets;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.WebSocket;

/// <summary>
/// WebSocket transport helper for creating multiplexers over WebSocket connections.
/// </summary>
public static class WebSocketMultiplexer
{
    /// <summary>
    /// Creates multiplexer options that connect to the specified WebSocket URI.
    /// Supports reconnection — each call to StreamFactory creates a new WebSocket connection.
    /// </summary>
    /// <param name="uri">The WebSocket URI to connect to.</param>
    /// <param name="clientOptions">Optional action to configure WebSocket client options.</param>
    /// <returns>MultiplexerOptions configured for WebSocket client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        Uri uri,
        Action<ClientWebSocketOptions>? clientOptions = null)
    {
        ValidateClientUri(uri, nameof(uri));

        return CreateOptionsCore(uri, clientOptions);
    }

    private static void ValidateClientUri(Uri uri, string paramName)
    {
        ArgumentNullException.ThrowIfNull(uri, paramName);

        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("WebSocket URI must be absolute.", paramName);

        if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("WebSocket URI scheme must be 'ws' or 'wss'.", paramName);
        }
    }

    private static MultiplexerOptions CreateOptionsCore(
        Uri uri,
        Action<ClientWebSocketOptions>? clientOptions)
    {
        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var webSocket = new ClientWebSocket();
                clientOptions?.Invoke(webSocket.Options);
                try
                {
                    await webSocket.ConnectAsync(uri, ct).ConfigureAwait(false);
                    var stream = new WebSocketStream(webSocket);
                    return new StreamPair(stream, webSocket);
                }
                catch
                {
                    webSocket.Dispose();
                    throw;
                }
            }
        };
    }

    /// <summary>
    /// Creates multiplexer options that connect to the specified WebSocket URL.
    /// Supports reconnection — each call to StreamFactory creates a new WebSocket connection.
    /// </summary>
    /// <param name="url">The WebSocket URL to connect to.</param>
    /// <param name="clientOptions">Optional action to configure WebSocket client options.</param>
    /// <returns>MultiplexerOptions configured for WebSocket client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string url,
        Action<ClientWebSocketOptions>? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("WebSocket URL must be an absolute URI.", nameof(url));

        ValidateClientUri(uri, nameof(url));
        return CreateOptionsCore(uri, clientOptions);
    }

    /// <summary>
    /// Creates multiplexer options for an already-accepted WebSocket connection (server-side).
    /// Reconnection is not supported for server-side connections.
    /// </summary>
    /// <param name="webSocket">The accepted WebSocket connection.</param>
    /// <returns>MultiplexerOptions configured for the accepted WebSocket.</returns>
    public static MultiplexerOptions CreateServerOptions(
        System.Net.WebSockets.WebSocket webSocket)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        var accepted = false;
        return new MultiplexerOptions
        {
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
    }
}
