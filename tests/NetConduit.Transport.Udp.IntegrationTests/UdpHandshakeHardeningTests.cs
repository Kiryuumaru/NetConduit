using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

// Regression coverage for Issue #616 residual (server-side Phase 1 hardening).
// New files only — no existing test is modified.
public class UdpHandshakeHardeningTests
{
    private static readonly byte[] HelloPayload = "NC_HELLO"u8.ToArray();

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static UdpClient MakeLoopbackClient(int port)
    {
        var c = new UdpClient(AddressFamily.InterNetworkV6);
        c.Client.DualMode = true;
        c.Connect(new IPEndPoint(IPAddress.IPv6Loopback, port));
        return c;
    }

    private static async Task RoundTripDataAsync(StreamMultiplexer client, StreamMultiplexer server, CancellationToken ct)
    {
        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", ct);

        var testData = "Hello, hardened UDP multiplexer!"u8.ToArray();
        await writeChannel.WriteAsync(testData, ct);
        await writeChannel.CloseAsync(ct);

        var buffer = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, buffer);
    }

    [Fact(Timeout = 30000)]
    public async Task RogueHelloIdle_DoesNotLockOutLegitWithinWindow()
    {
        // Holmes PoC shape, but the legit side is a full client: a single exact
        // NC_HELLO from a rogue that then goes silent must not spend the one-shot.
        // The live (retransmitting) legit client within the verification window
        // migrates the tentative commit and gets a working session.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        await Task.Delay(150, cts.Token);

        using (var rogue = MakeLoopbackClient(port))
        {
            await rogue.SendAsync(HelloPayload, cts.Token);
            await Task.Delay(50, cts.Token);
        }

        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await RoundTripDataAsync(client, server, cts.Token);
    }

    [Fact(Timeout = 30000)]
    public async Task HelloFlood_Bounded_LegitStillConnects()
    {
        // Single-shot HELLO spray (many endpoints, no retransmit — the blind-spoof
        // shape) must not wedge the accept path: the table stays O(MaxCandidates)
        // and the live legit client still connects within budget.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        await Task.Delay(150, cts.Token);

        var sprayers = new List<UdpClient>();
        try
        {
            for (int i = 0; i < 40; i++)
                sprayers.Add(MakeLoopbackClient(port));
            foreach (var s in sprayers)
                await s.SendAsync(HelloPayload, cts.Token);

            client.Start();
            await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

            Assert.True(client.IsConnected);
            Assert.True(server.IsConnected);

            await RoundTripDataAsync(client, server, cts.Token);
        }
        finally
        {
            foreach (var s in sprayers)
                s.Dispose();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ChallengeOptIn_PreservesV1WireCompat()
    {
        // OptIn accepts v1: an old (existing-bytes) client still connects.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverOptions = UdpMultiplexer.CreateServerOptions(
            port,
            acceptOptions: new UdpAcceptOptions { ChallengeMode = UdpChallengeMode.OptIn });
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact(Timeout = 30000)]
    public async Task ChallengeRequired_RejectsV1HelloLoudly()
    {
        // Required never issues NC_HELLO_ACK to a legacy handshake: loud
        // InvalidOperationException, never a silent stall.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = UdpMultiplexer.CreateServerOptions(
            port,
            acceptOptions: new UdpAcceptOptions { ChallengeMode = UdpChallengeMode.Required });
        var serverTask = serverOptions.StreamFactory!(cts.Token);
        await Task.Delay(150, cts.Token);

        using var raw = MakeLoopbackClient(port);
        await raw.SendAsync(HelloPayload, cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Required", ex.Message, StringComparison.Ordinal);
    }
}

public class UdpAcceptTrackerTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public void AdmitHello_BoundsTable_LruEviction()
    {
        var tracker = new UdpAcceptTracker(new UdpAcceptOptions { MaxCandidates = 4 });
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8; i++)
            Assert.True(tracker.AdmitHello(Ep(1000 + i), now));

        Assert.Equal(4, tracker.CandidateCount);
        Assert.False(tracker.IsKnown(Ep(1000)));
        Assert.True(tracker.IsKnown(Ep(1007)));
    }

    [Fact]
    public void AdmitHello_PerEndpointCap_DropsOverLimit()
    {
        var tracker = new UdpAcceptTracker(new UdpAcceptOptions { MaxHellosPerEndpointPerSecond = 3 });
        var now = DateTimeOffset.UtcNow;
        Assert.True(tracker.AdmitHello(Ep(2000), now));
        Assert.True(tracker.AdmitHello(Ep(2000), now));
        Assert.True(tracker.AdmitHello(Ep(2000), now));
        Assert.False(tracker.AdmitHello(Ep(2000), now));
    }

    [Fact]
    public void AdmitHello_GlobalCap_DropsOverLimit()
    {
        var tracker = new UdpAcceptTracker(new UdpAcceptOptions { MaxHellosGlobalPerSecond = 5 });
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
            Assert.True(tracker.AdmitHello(Ep(3000 + i), now));
        Assert.False(tracker.AdmitHello(Ep(3999), now));
    }

    [Fact]
    public void LiveRequiresRetransmitOrData()
    {
        var tracker = new UdpAcceptTracker(new UdpAcceptOptions());
        var now = DateTimeOffset.UtcNow;
        Assert.True(tracker.AdmitHello(Ep(4000), now));
        Assert.False(tracker.IsLive(Ep(4000)));

        Assert.True(tracker.AdmitHello(Ep(4000), now.AddMilliseconds(200)));
        Assert.True(tracker.IsLive(Ep(4000)));
    }
}
