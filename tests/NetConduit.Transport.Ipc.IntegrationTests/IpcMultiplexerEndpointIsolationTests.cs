using System.Net.Sockets;
using NetConduit.Transport.Ipc;

namespace NetConduit.Transport.Ipc.IntegrationTests;

/// <summary>
/// Two distinct endpoint names must NEVER address the same IPC
/// listener. The legacy Windows implementation hashed the endpoint name to a
/// 16-bit value modulo a 16,383-port window; the birthday bound made colliding
/// endpoint names trivial to construct and a client targeting endpoint A could
/// silently land on the server bound to endpoint B, both speaking the same
/// multiplexer protocol.
/// </summary>
public class IpcMultiplexerEndpointIsolationTests
{
    [Fact(Timeout = 30000)]
    public async Task DistinctEndpoints_DoNotCrossConnect()
    {
        var endpointA = MakeEndpoint("alpha");
        var endpointB = MakeEndpoint("beta");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverAOptions = IpcMultiplexer.CreateServerOptions(endpointA);
        await using var serverA = StreamMultiplexer.Create(serverAOptions);
        serverA.Start();

        // Give the listener a moment to bind.
        await Task.Delay(200, cts.Token);

        // Client targets endpoint B. No server is bound to B. The client must NOT
        // somehow reach serverA. Pre-fix on Windows, a name pair whose 16-bit hash
        // collided would do exactly that — silently completing the handshake against
        // an unrelated server.
        var clientBOptions = IpcMultiplexer.CreateOptions(endpointB);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var pair = await clientBOptions.StreamFactory(connectCts.Token);
            await pair.DisposeAsync();
        });

        // serverA must still be alive and unaffected.
        Assert.True(serverA.IsRunning);
    }

    [Fact(Timeout = 30000)]
    public async Task DuplicateServerOnSameEndpoint_FailsCleanly()
    {
        var endpoint = MakeEndpoint("dup");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var firstOptions = IpcMultiplexer.CreateServerOptions(endpoint);
        await using var first = StreamMultiplexer.Create(firstOptions);
        first.Start();

        await Task.Delay(200, cts.Token);

        // A second listener on the same endpoint must fail at bind, not silently
        // succeed (which would let it racy-steal connections destined for `first`).
        var secondOptions = IpcMultiplexer.CreateServerOptions(endpoint);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var bindCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            bindCts.CancelAfter(TimeSpan.FromSeconds(2));
            var pair = await secondOptions.StreamFactory(bindCts.Token);
            await pair.DisposeAsync();
        });
    }

    private static string MakeEndpoint(string tag)
    {
        var unique = $"nc-iso-{tag}-{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
            return unique;
        return Path.Combine(Path.GetTempPath(), unique + ".sock");
    }
}
