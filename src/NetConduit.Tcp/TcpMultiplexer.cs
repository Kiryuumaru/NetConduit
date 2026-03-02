using System.Net;
using System.Net.Sockets;

namespace NetConduit.Tcp;

/// <summary>
/// TCP transport helper for creating multiplexers over TCP connections.
/// </summary>
public static class TcpMultiplexer
{
    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified host and port.
    /// Supports reconnection - each call to StreamFactory creates a new TCP connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="configure">Optional action to configure additional options.</param>
    /// <returns>MultiplexerOptions configured for TCP client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        
        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
        
        configure?.Invoke(options);
        return options;
    }
    
    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified endpoint.
    /// Supports reconnection - each call to StreamFactory creates a new TCP connection.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <param name="configure">Optional action to configure additional options.</param>
    /// <returns>MultiplexerOptions configured for TCP client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        IPEndPoint endpoint,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        
        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
        
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Creates multiplexer options with a StreamFactory that accepts connections from the specified listener.
    /// Reconnection is disabled by default for server-side connections.
    /// </summary>
    /// <param name="listener">The TCP listener to accept from.</param>
    /// <param name="configure">Optional action to configure additional options.</param>
    /// <returns>MultiplexerOptions configured for TCP server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(
        TcpListener listener,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        
        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = async ct =>
            {
                if (accepted)
                {
                    throw new InvalidOperationException(
                        "Server-side multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }
                
                accepted = true;
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
        
        configure?.Invoke(options);
        return options;
    }
}
