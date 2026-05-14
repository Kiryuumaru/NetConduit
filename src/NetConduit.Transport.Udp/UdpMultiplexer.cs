using System.Net;
using System.Net.Sockets;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Udp;

/// <summary>
/// UDP transport helper with a reliable stream shim for use with StreamMultiplexer.
/// </summary>
public static class UdpMultiplexer
{
    private static readonly byte[] HelloPayload = "NC_HELLO"u8.ToArray();
    private static readonly byte[] HelloAckPayload = "NC_HELLO_ACK"u8.ToArray();

    /// <summary>
    /// Creates multiplexer options that connect to the specified UDP endpoint.
    /// Supports reconnection — each call to StreamFactory creates a new UDP connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="udpOptions">Optional reliable UDP stream options.</param>
    /// <returns>MultiplexerOptions configured for UDP client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        ReliableUdpOptions? udpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new UdpClient(AddressFamily.InterNetworkV6);
                try
                {
                    client.Client.DualMode = true;
                    await client.Client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                    await client.SendAsync(HelloPayload, ct).ConfigureAwait(false);
                    await TryReceiveHelloAckAsync(client, ct).ConfigureAwait(false);

                    var reliable = new ReliableUdpStream(client, udpOptions);
                    return new StreamPair(reliable);
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
    /// Creates multiplexer options that accept UDP connections on the specified port.
    /// Reconnection is not supported for server-side connections.
    /// </summary>
    /// <param name="listenPort">The port to listen on.</param>
    /// <param name="udpOptions">Optional reliable UDP stream options.</param>
    /// <returns>MultiplexerOptions configured for UDP server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(
        int listenPort,
        ReliableUdpOptions? udpOptions = null)
    {
        var accepted = false;
        return new MultiplexerOptions
        {
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
                try
                {
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
                catch
                {
                    listener.Dispose();
                    throw;
                }
            }
        };
    }

    private static async Task TryReceiveHelloAckAsync(UdpClient client, CancellationToken cancellationToken)
    {
        const int maxRetries = 20;
        const int retryDelayMs = 200;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(retryDelayMs));
                var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (result.Buffer.AsSpan().SequenceEqual(HelloAckPayload))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await client.SendAsync(HelloPayload, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                await client.SendAsync(HelloPayload, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
