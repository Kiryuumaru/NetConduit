using System.Text;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for #237 — pre-handshake OpenChannel must not allocate a
/// wire index from the default odd parity. Both peers' Create(...) factories
/// seed _nextChannelIndex to 1; the real parity is only decided by the
/// handshake's session-GUID comparison. If allocation runs before the
/// handshake flips one side to even, both sides bake index 1 into queued
/// INIT frames and the resulting INIT-ACK arrives on the wrong local
/// channel — WriteChannel.WaitForReadyAsync never completes.
/// </summary>
public sealed class PreHandshakeChannelIndexTests
{
    // Forces the handshake to flip the client (lower GUID) to even and keep
    // the server (higher GUID) odd.
    private static readonly Guid LowerSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HigherSessionId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            SessionId = LowerSessionId,
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            SessionId = HigherSessionId,
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        return (client, server);
    }

    [Fact]
    public async Task OpenChannel_BeforeHandshake_BothSidesReachReady()
    {
        // Pre-fix: both sides default _nextChannelIndex to 1, so client and
        // server each allocate index 1 for their pre-handshake OpenChannel.
        // The peer's INIT-ACK arrives on a wire-index that the local registry
        // already binds to a different role — the local WriteChannel never
        // observes its INIT-ACK and WaitForReadyAsync hangs forever.
        //
        // Repeat across many iterations so any single accidentally-fast
        // handshake cannot mask the bug.
        const int iterations = 20;
        for (int i = 0; i < iterations; i++)
        {
            var (client, server) = CreatePair();
            try
            {
                client.Start();
                server.Start();

                // Issue both opens on the thread pool so the test thread can
                // drive both peers' handshakes via WaitForReadyAsync below.
                // With the fix, OpenChannel blocks on _paritySignal until
                // SetIndexParity runs; without it, OpenChannel returns
                // immediately with a corrupt-parity index baked into the
                // queued INIT frame.
                var clientOpen = Task.Run(() => client.OpenChannel($"c-{i}"));
                var serverOpen = Task.Run(() => server.OpenChannel($"s-{i}"));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

                var clientWrite = await clientOpen;
                var serverWrite = await serverOpen;

                // INIT-ACK round-trip must succeed for both write channels.
                // This is what hangs pre-fix.
                await clientWrite.WaitForReadyAsync(cts.Token);
                await serverWrite.WaitForReadyAsync(cts.Token);
            }
            finally
            {
                await client.DisposeAsync();
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task OpenChannel_BeforeHandshake_RoutesEndToEndDataCorrectly()
    {
        // End-to-end smoke: with the fix, a pre-ready open on both sides
        // completes a normal data round trip. Pre-fix this either hangs in
        // WaitForReadyAsync (the WriteChannel never observes its INIT-ACK)
        // or — when the channels do reach Ready by accident — silently
        // delivers the wrong bytes because of the index-1 collision.
        var (client, server) = CreatePair();
        try
        {
            client.Start();
            server.Start();

            var clientOpen = Task.Run(() => client.OpenChannel("client-stream"));
            var serverOpen = Task.Run(() => server.OpenChannel("server-stream"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

            var clientWrite = await clientOpen;
            var serverWrite = await serverOpen;

            await clientWrite.WaitForReadyAsync(cts.Token);
            await serverWrite.WaitForReadyAsync(cts.Token);

            var serverRead = await server.AcceptChannelAsync("client-stream", cts.Token);
            var clientRead = await client.AcceptChannelAsync("server-stream", cts.Token);

            byte[] fromClient = Encoding.UTF8.GetBytes("payload-from-client");
            byte[] fromServer = Encoding.UTF8.GetBytes("payload-from-server");

            await clientWrite.WriteAsync(fromClient, cts.Token);
            await serverWrite.WriteAsync(fromServer, cts.Token);

            byte[] serverBuf = new byte[fromClient.Length];
            await ReadExactAsync(serverRead, serverBuf, cts.Token);
            Assert.Equal(fromClient, serverBuf);

            byte[] clientBuf = new byte[fromServer.Length];
            await ReadExactAsync(clientRead, clientBuf, cts.Token);
            Assert.Equal(fromServer, clientBuf);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task ReadExactAsync(IReadChannel channel, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await channel.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0)
                throw new InvalidOperationException("Channel ended before expected payload was read.");
            total += n;
        }
    }
}
