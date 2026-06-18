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
                    await ReceiveHelloAckAsync(client, ct).ConfigureAwait(false);

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
                        "Server-side UDP multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }
                if (prev == 1)
                {
                    throw new InvalidOperationException(
                        "Server-side UDP multiplexer is already accepting a connection.");
                }

                var listener = new UdpClient(AddressFamily.InterNetworkV6);
                try
                {
                    listener.Client.DualMode = true;
                    listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));

                    // Discard datagrams that are not NC_HELLO before latching the
                    // socket. Without this loop a single stray UDP packet (port
                    // scanner, leftover from a previous session, attacker probe,
                    // health check) would Connect() the listener to a non-client
                    // endpoint and permanently DoS the server.
                    IPEndPoint remote;
                    while (true)
                    {
                        var result = await listener.ReceiveAsync(ct).ConfigureAwait(false);
                        if (result.Buffer.AsSpan().SequenceEqual(HelloPayload))
                        {
                            remote = result.RemoteEndPoint;
                            break;
                        }
                    }

                    listener.Connect(remote);
                    await listener.SendAsync(HelloAckPayload, ct).ConfigureAwait(false);

                    // Drain any duplicate NC_HELLO datagrams the client retransmitted
                    // while its first send was still in flight. Without this,
                    // the leftover HELLO bytes sit in the kernel buffer and the next
                    // ReliableUdpStream.ReadAsync delivers "NC_HELLO" as protocol
                    // bytes, which parse as a bogus frame header and either fault the
                    // mux with a framing error or hang waiting for a huge payload.
                    // A non-NC_HELLO datagram on the connected socket is the first
                    // real protocol frame and must be left untouched for the stream.
                    await DrainPendingHellosAsync(listener, ct).ConfigureAwait(false);

                    var reliable = new ReliableUdpStream(listener, udpOptions);
                    Interlocked.Exchange(ref state, 2);
                    return new StreamPair(reliable);
                }
                catch
                {
                    listener.Dispose();
                    Interlocked.CompareExchange(ref state, 0, 1);
                    throw;
                }
            }
        };
    }

    private static async Task ReceiveHelloAckAsync(UdpClient client, CancellationToken cancellationToken)
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

                // Silently discarding non-ACK datagrams would lose application
                // data permanently if the server's first frame races ahead of
                // (or replaces) the ACK on the wire. The server only ever
                // emits NC_HELLO_ACK before the application protocol starts, so
                // anything else here is either a stale retransmit from a previous
                // session on the same port or a protocol error — either way the
                // safe action is to fail loudly rather than swallow the bytes.
                throw new InvalidOperationException(
                    $"UDP handshake received unexpected {result.Buffer.Length}-byte datagram before NC_HELLO_ACK.");
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

        throw new TimeoutException(
            $"UDP transport did not receive NC_HELLO_ACK from the remote endpoint after {maxRetries} attempts.");
    }

    // After the server sends NC_HELLO_ACK, the client's retransmit loop may have
    // already queued additional NC_HELLO datagrams in the server's kernel buffer
    // (one retransmit per ~200 ms ack-wait). They must be discarded before
    // ReliableUdpStream takes over the socket, or they will be read as protocol
    // bytes. The drain stops on the first non-HELLO datagram (that one is the
    // peer's first real protocol frame and must be preserved for the stream)
    // or when the socket has no more data immediately available.
    private static async Task DrainPendingHellosAsync(UdpClient listener, CancellationToken ct)
    {
        // Bound the drain so a peer that keeps spraying NC_HELLO cannot wedge
        // the accept path forever. The client's documented maximum is 21
        // datagrams (1 initial + 20 retries); 64 is a generous ceiling.
        const int MaxDrainedDatagrams = 64;

        for (int i = 0; i < MaxDrainedDatagrams; i++)
        {
            if (listener.Available <= 0)
                return;

            UdpReceiveResult next;
            try
            {
                next = await listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Available raced with another reader (there should be none on
                // a freshly-connected client at this point) or the socket was
                // torn down; nothing to drain, let the stream take over.
                return;
            }

            if (!next.Buffer.AsSpan().SequenceEqual(HelloPayload))
            {
                // Not a duplicate HELLO — this is the peer's first protocol
                // frame. UdpClient gives no way to push it back into the receive
                // queue, so we must surface this rather than drop it. The peer
                // sent application bytes before observing our ACK, which is a
                // handshake violation under the documented protocol (the
                // client side waits for ACK before constructing its stream).
                throw new InvalidOperationException(
                    $"UDP peer sent {next.Buffer.Length}-byte non-handshake datagram before observing NC_HELLO_ACK; dropping connection to avoid silent data loss.");
            }
        }
    }
}
