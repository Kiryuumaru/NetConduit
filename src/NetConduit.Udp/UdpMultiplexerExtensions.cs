using System.Net.Sockets;
using NetConduit;

namespace NetConduit.Udp;

/// <summary>
/// Extension methods for UDP clients.
/// </summary>
public static class UdpMultiplexerExtensions
{
    /// <summary>
    /// Connects a UDP client and wraps it in a multiplexer.
    /// </summary>
    public static async Task<UdpMultiplexerConnection> ConnectMuxAsync(
        this UdpClient client,
        string host,
        int port,
        ReliableUdpOptions? udpOptions = null,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await client.Client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await client.SendAsync("NC_HELLO"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await TryReceiveHelloAckAsync(client, cancellationToken).ConfigureAwait(false);

        var stream = new ReliableUdpStream(client, udpOptions);
        var mux = new StreamMultiplexer(stream, stream, options);
        return new UdpMultiplexerConnection(mux, client, stream);
    }

    private static async Task TryReceiveHelloAckAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            _ = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
}
