using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

// Server-side accept-policy coverage (wire-compat Phase 1).
// Additive only — no existing test or src/ file is modified.
//
// Pins as-built accept policy: a singleton HELLO-alone commits at its
// verification deadline (the pre-existing HELLO-alone wire contract), an
// idle server waits on the caller's token (no internal global timeout),
// the candidate table stays bounded under spray, and the ChallengeMode
// matrix interops loudly-or-compatibly with v1 bytes.
public class UdpHandshakeAcceptanceTests
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

        var testData = "Hello, accepted UDP multiplexer!"u8.ToArray();
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
    public async Task SingletonHelloAlone_CommitsAtDeadline()
    {
        // As-built policy: a single admitted HELLO with no follow-up still
        // commits the one-shot at its verification deadline (the wire
        // contract raw-HELLO retransmit loops rely on). The factory must
        // therefore complete shortly after a ~300ms window even though the
        // lone sender never proves liveness.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverOptions = UdpMultiplexer.CreateServerOptions(
            port,
            acceptOptions: new UdpAcceptOptions { VerificationWindow = TimeSpan.FromMilliseconds(300) });
        var serverTask = serverOptions.StreamFactory!(cts.Token);
        await Task.Delay(150, cts.Token);

        using (var lone = MakeLoopbackClient(port))
        {
            await lone.SendAsync(HelloPayload, cts.Token);
        }

        await using var pair = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(pair);
    }

    [Fact(Timeout = 30000)]
    public async Task IdleServer_WaitsOnCallerToken_ThenServesLegit()
    {
        // No internal global timeout: with zero HELLOs the factory stays
        // parked on the caller's token (not completed after ~700ms, well
        // past two verification windows), and a later full client still gets
        // a working session with literal end-to-end bytes.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        await Task.Delay(700, cts.Token);

        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await RoundTripDataAsync(client, server, cts.Token);
    }

    [Fact(Timeout = 30000)]
    public async Task FloodBeyondCapacity_LegitStillConnects()
    {
        // Bounded table under real eviction pressure: MaxCandidates=8 with
        // 32 distinct single-shot endpoints (4x capacity, the blind-spoof
        // shape) must not wedge accept; the live retransmitting legit client
        // still connects with literal end-to-end bytes.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var serverOptions = UdpMultiplexer.CreateServerOptions(
            port,
            acceptOptions: new UdpAcceptOptions { MaxCandidates = 8 });
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        await Task.Delay(150, cts.Token);

        var sprayers = new List<UdpClient>();
        try
        {
            for (int i = 0; i < 32; i++)
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
    public async Task ChallengeDisabled_PreservesV1Session()
    {
        // Disabled (default Phase 1) + v1 bytes: full session with literal
        // end-to-end bytes — the explicit as-shipped interop baseline.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverOptions = UdpMultiplexer.CreateServerOptions(
            port,
            acceptOptions: new UdpAcceptOptions { ChallengeMode = UdpChallengeMode.Disabled });
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await RoundTripDataAsync(client, server, cts.Token);
    }

    [Fact(Timeout = 30000)]
    public async Task ChallengeOptIn_ServesV1Session()
    {
        // New-server/old-client interop: OptIn still serves a v1 client a
        // full session with literal end-to-end bytes.
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

        await RoundTripDataAsync(client, server, cts.Token);
    }

    [Fact(Timeout = 30000)]
    public async Task ChallengeRequired_RejectsV1HelloLoudly()
    {
        // Required never issues NC_HELLO_ACK to a legacy handshake: loud
        // InvalidOperationException naming the mismatch, never a silent stall.
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
        Assert.Contains("NC_HELLO_ACK", ex.Message, StringComparison.Ordinal);
    }
}
