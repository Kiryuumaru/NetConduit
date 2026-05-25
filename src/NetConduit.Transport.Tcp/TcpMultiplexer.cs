using System.Net;
using System.Net.Sockets;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Tcp;

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
                try
                {
                    await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                    var stream = client.GetStream();
                    return new StreamPair(stream, client);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
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
                try
                {
                    await client.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    var stream = client.GetStream();
                    return new StreamPair(stream, client);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
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

        // 0 = idle, 1 = accepting, 2 = accepted
        var state = 0;
        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var prev = Interlocked.CompareExchange(ref state, 1, 0);
                if (prev == 2)
                {
                    throw new InvalidOperationException(
                        "Server-side multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }
                if (prev == 1)
                {
                    throw new InvalidOperationException(
                        "Server-side multiplexer is already accepting a connection.");
                }

                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // NoDelay setter and GetStream() can both throw if the peer
                        // RST'd in the window between accept and configuration
                        // (e.g. SocketException(WSAECONNRESET) from NoDelay, or
                        // InvalidOperationException from GetStream when Socket.Connected
                        // is already false). Without an inner try, the accepted client
                        // would leak its kernel socket handle until finalization.
                        client.NoDelay = true;
                        var stream = client.GetStream();
                        Interlocked.Exchange(ref state, 2);
                        return new StreamPair(stream, client);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                }
                catch
                {
                    Interlocked.CompareExchange(ref state, 0, 1);
                    throw;
                }
            }
        };
    }
}
