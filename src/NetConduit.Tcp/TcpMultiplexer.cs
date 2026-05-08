using System.Net;
using System.Net.Sockets;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Tcp;

/// <summary>
/// TCP transport helper for creating multiplexers over TCP connections.
/// </summary>
public static class TcpMultiplexer
{
    /// <summary>
    /// Creates multiplexer options that connect to the specified host and port.
    /// Supports reconnection — each call to StreamFactory creates a new TCP connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <returns>MultiplexerOptions configured for TCP client connection.</returns>
    public static MultiplexerOptions CreateOptions(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);

        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
    }

    /// <summary>
    /// Creates multiplexer options that connect to the specified endpoint.
    /// Supports reconnection — each call to StreamFactory creates a new TCP connection.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <returns>MultiplexerOptions configured for TCP client connection.</returns>
    public static MultiplexerOptions CreateOptions(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
    }

    /// <summary>
    /// Creates multiplexer options that accept a connection from the specified listener.
    /// Reconnection is not supported for server-side connections.
    /// </summary>
    /// <param name="listener">The TCP listener to accept from.</param>
    /// <returns>MultiplexerOptions configured for TCP server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(TcpListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        var accepted = false;
        return new MultiplexerOptions
        {
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
                client.NoDelay = true;
                var stream = client.GetStream();
                return new StreamPair(stream, client);
            }
        };
    }
}
