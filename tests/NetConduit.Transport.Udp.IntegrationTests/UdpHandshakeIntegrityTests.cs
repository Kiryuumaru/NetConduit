using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

// Regression coverage for. Two distinct defects in the UDP handshake:
//
//   1. Server-side: a client's NC_HELLO retransmit (sent because the 200 ms
//      ack-wait fired before the ACK arrived) was left in the kernel buffer
//      after the server's first ReceiveAsync returned. ReliableUdpStream then
//      read the duplicate NC_HELLO bytes as protocol bytes, corrupting framing.
//
//   2. Client-side: ReceiveHelloAckAsync silently discarded any datagram whose
//      buffer was not exactly NC_HELLO_ACK. A real protocol frame that raced
//      ahead of the ACK was therefore lost permanently while the client kept
//      waiting for an ACK it had already missed.
public class UdpHandshakeIntegrityTests
{
    private static readonly byte[] HelloPayload = "NC_HELLO"u8.ToArray();
    private static readonly byte[] HelloAckPayload = "NC_HELLO_ACK"u8.ToArray();

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_ThrowsWhenNonHelloDatagramQueuedDuringHandshake()
    {
        // Reproduces the server-side leg of. A misbehaving (or malicious)
        // client sends NC_HELLO followed immediately by a non-handshake
        // datagram before the server has finished its handshake. Pre-fix the
        // server reads only the first datagram, sends NC_HELLO_ACK, then
        // hands the same UdpClient — still holding the bogus datagram in its
        // kernel buffer — directly to ReliableUdpStream. Those bytes are then
        // fed to the protocol state machine as data. Post-fix the server
        // drains any pending datagrams that follow NC_HELLO and rejects the
        // connection with InvalidOperationException if a non-handshake
        // datagram is observed before the peer could have seen the ACK.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);

        // Start the server factory so the listener socket is bound and parked
        // on ReceiveAsync before we send anything from bursty. Without this
        // the HELLO arrives at a closed port and is silently dropped.
        var factoryTask = Task.Run(async () =>
        {
            var pair = await serverOptions.StreamFactory(cts.Token);
            await pair.DisposeAsync();
        }, cts.Token);

        await Task.Delay(150, cts.Token);

        using var bursty = new UdpClient(AddressFamily.InterNetworkV6);
        bursty.Client.DualMode = true;
        await bursty.Client.ConnectAsync(IPAddress.IPv6Loopback, port, cts.Token);

        // NC_HELLO + a bogus 8-byte datagram back-to-back. The bogus payload
        // lands in the server's kernel buffer before the server's drain runs.
        await bursty.SendAsync(HelloPayload, cts.Token);
        await bursty.SendAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE }, cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await factoryTask);

        Assert.Contains("non-handshake", ex.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task ClientFactory_ThrowsOnNonAckDatagramDuringHandshake()
    {
        // Reproduces the client-side leg of. A rogue "server" listens on
        // the port, receives NC_HELLO, then immediately sends a non-ACK
        // payload (e.g. a stray protocol frame). Pre-fix, the client silently
        // discarded that datagram and kept retransmitting NC_HELLO until the
        // 4-second timeout fired, losing the application bytes. Post-fix, the
        // client surfaces InvalidOperationException so the caller sees the
        // protocol violation immediately.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var rogue = new UdpClient(AddressFamily.InterNetworkV6);
        rogue.Client.DualMode = true;
        rogue.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));

        var rogueTask = Task.Run(async () =>
        {
            var first = await rogue.ReceiveAsync(cts.Token);
            // Send a single application-shaped payload (definitely not NC_HELLO_ACK).
            var bogus = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            await rogue.Client.SendToAsync(bogus, SocketFlags.None, first.RemoteEndPoint, cts.Token);
        }, cts.Token);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var pair = await clientOptions.StreamFactory(cts.Token);
            await pair.DisposeAsync();
        });

        Assert.Contains("NC_HELLO_ACK", ex.Message, StringComparison.Ordinal);

        await rogueTask;
    }
}
