using System.Net;
using System.Net.Sockets;
using NetConduit;

namespace NetConduit.Udp;

/// <summary>
/// UDP transport helper that wraps a minimal reliable stream shim for use with StreamMultiplexer.
/// </summary>
public static class UdpMultiplexer
{
    private static readonly byte[] HelloPayload = "NC_HELLO"u8.ToArray();
    private static readonly byte[] HelloAckPayload = "NC_HELLO_ACK"u8.ToArray();

    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified UDP endpoint.
    /// Supports reconnection - each call to StreamFactory creates a new UDP connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="udpOptions">Optional reliable UDP stream options.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for UDP client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        ReliableUdpOptions? udpOptions = null,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new UdpClient(AddressFamily.InterNetworkV6);
                client.Client.DualMode = true;
                await client.Client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                await client.SendAsync(HelloPayload, ct).ConfigureAwait(false);
                await TryReceiveHelloAckAsync(client, ct).ConfigureAwait(false);

                var reliable = new ReliableUdpStream(client, udpOptions);
                return new StreamPair(reliable);
            }
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Creates multiplexer options with a StreamFactory that accepts UDP connections on the specified port.
    /// Reconnection is disabled by default for server-side connections.
    /// </summary>
    /// <param name="listenPort">The port to listen on.</param>
    /// <param name="udpOptions">Optional reliable UDP stream options.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for UDP server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(
        int listenPort,
        ReliableUdpOptions? udpOptions = null,
        Action<MultiplexerOptions>? configure = null)
    {
        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = async ct =>
            {
                if (accepted)
                {
                    throw new InvalidOperationException(
                        "Server-side UDP multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }

                accepted = true;

                var listener = new UdpClient(AddressFamily.InterNetworkV6);
                listener.Client.DualMode = true;
                listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));

                var result = await listener.ReceiveAsync(ct).ConfigureAwait(false);
                var remote = result.RemoteEndPoint;
                listener.Connect(remote);

                if (result.Buffer.AsSpan().SequenceEqual(HelloPayload))
                {
                    await listener.SendAsync(HelloAckPayload, ct).ConfigureAwait(false);
                }

                var reliable = new ReliableUdpStream(listener, udpOptions);
                return new StreamPair(reliable);
            }
        };

        configure?.Invoke(options);
        return options;
    }

    private static async Task TryReceiveHelloAckAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            _ = result;
        }
        catch
        {
            // Best-effort: ignore timeouts/failures
        }
    }
}
