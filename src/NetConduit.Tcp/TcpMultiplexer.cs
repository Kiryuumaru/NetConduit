using System.Net;
using System.Net.Sockets;

namespace NetConduit.Tcp;

/// <summary>
/// TCP transport helper for creating multiplexers over TCP connections.
/// </summary>
public static class TcpMultiplexer
{
    /// <summary>
    /// Connects to a TCP server and creates a multiplexer.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying TCP client.</returns>
    public static async Task<TcpMultiplexerConnection> ConnectAsync(
        string host,
        int port,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var multiplexer = new StreamMultiplexer(stream, stream, options);
            return new TcpMultiplexerConnection(multiplexer, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects to a TCP server and creates a multiplexer.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying TCP client.</returns>
    public static async Task<TcpMultiplexerConnection> ConnectAsync(
        IPEndPoint endpoint,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var multiplexer = new StreamMultiplexer(stream, stream, options);
            return new TcpMultiplexerConnection(multiplexer, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts a TCP connection and creates a multiplexer.
    /// </summary>
    /// <param name="listener">The TCP listener to accept from.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying TCP client.</returns>
    public static async Task<TcpMultiplexerConnection> AcceptAsync(
        TcpListener listener,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listener);
        
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = client.GetStream();
            var multiplexer = new StreamMultiplexer(stream, stream, options);
            return new TcpMultiplexerConnection(multiplexer, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a multiplexer from an existing TCP client.
    /// </summary>
    /// <param name="client">The TCP client.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <returns>A multiplexer wrapping the TCP client.</returns>
    public static TcpMultiplexerConnection FromClient(
        TcpClient client,
        MultiplexerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        
        var stream = client.GetStream();
        var multiplexer = new StreamMultiplexer(stream, stream, options);
        return new TcpMultiplexerConnection(multiplexer, client);
    }
}
