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
    /// Connects to a UDP endpoint and creates a multiplexer.
    /// </summary>
    public static async Task<UdpMultiplexerConnection> ConnectAsync(
        string host,
        int port,
        ReliableUdpOptions? udpOptions = null,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var client = new UdpClient(AddressFamily.InterNetworkV6);
        client.Client.DualMode = true;
        try
        {
            await client.Client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(HelloPayload, cancellationToken).ConfigureAwait(false);
            await TryReceiveHelloAckAsync(client, cancellationToken).ConfigureAwait(false);

            var reliable = new ReliableUdpStream(client, udpOptions);
            var mux = new StreamMultiplexer(reliable, reliable, options);
            return new UdpMultiplexerConnection(mux, client, reliable);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts the first incoming UDP datagram on the specified port and creates a multiplexer bound to that peer.
    /// </summary>
    public static async Task<UdpMultiplexerConnection> AcceptAsync(
        int listenPort,
        ReliableUdpOptions? udpOptions = null,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var listener = new UdpClient(AddressFamily.InterNetworkV6);
        listener.Client.DualMode = true; // Allow IPv4/IPv6 clients
        listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));

        try
        {
            var result = await listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var remote = result.RemoteEndPoint;
            listener.Connect(remote);

            // Respond to hello if we got one
            if (result.Buffer.AsSpan().SequenceEqual(HelloPayload))
            {
                await listener.SendAsync(HelloAckPayload, cancellationToken).ConfigureAwait(false);
            }

            var reliable = new ReliableUdpStream(listener, udpOptions);
            var mux = new StreamMultiplexer(reliable, reliable, options);
            return new UdpMultiplexerConnection(mux, listener, reliable);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    private static async Task TryReceiveHelloAckAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            _ = result; // Ignore content; best-effort ack
        }
        catch
        {
            // Best-effort: ignore timeouts/failures
        }
    }
}
