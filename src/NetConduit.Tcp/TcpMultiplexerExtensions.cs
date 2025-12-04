using System.Net.Sockets;

namespace NetConduit.Tcp;

/// <summary>
/// Extension methods for creating multiplexers from TCP types.
/// </summary>
public static class TcpMultiplexerExtensions
{
    /// <summary>
    /// Accepts a TCP connection and creates a multiplexer.
    /// </summary>
    /// <param name="listener">The TCP listener to accept from.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying TCP client.</returns>
    /// <example>
    /// <code>
    /// var listener = new TcpListener(IPAddress.Any, 5000);
    /// listener.Start();
    /// var connection = await listener.AcceptMuxAsync();
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static async Task<TcpMultiplexerConnection> AcceptMuxAsync(
        this TcpListener listener,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await TcpMultiplexer.AcceptAsync(listener, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a multiplexer from an existing connected TCP client.
    /// </summary>
    /// <param name="client">The connected TCP client.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <returns>A multiplexer wrapping the TCP client.</returns>
    /// <example>
    /// <code>
    /// var client = new TcpClient();
    /// await client.ConnectAsync("localhost", 5000);
    /// var connection = client.AsMux();
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static TcpMultiplexerConnection AsMux(
        this TcpClient client,
        MultiplexerOptions? options = null)
    {
        return TcpMultiplexer.FromClient(client, options);
    }

    /// <summary>
    /// Connects the TCP client and creates a multiplexer.
    /// </summary>
    /// <param name="client">The TCP client (will be connected).</param>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="options">Optional multiplexer options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected multiplexer with the underlying TCP client.</returns>
    /// <example>
    /// <code>
    /// var client = new TcpClient();
    /// var connection = await client.ConnectMuxAsync("localhost", 5000);
    /// var runTask = await connection.StartAsync();
    /// </code>
    /// </example>
    public static async Task<TcpMultiplexerConnection> ConnectMuxAsync(
        this TcpClient client,
        string host,
        int port,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return TcpMultiplexer.FromClient(client, options);
    }
}
