using NetConduit.Constants;

namespace NetConduit.UnitTests;

public sealed class GoAwayTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        return (client, server);
    }

    [Fact]
    public async Task GoAway_SetsShuttingDown()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        Assert.False(client.IsShuttingDown);

        await client.GoAwayAsync();

        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RemoteSideReceivesGoAway()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var disconnectTcs = new TaskCompletionSource<DisconnectReason>();
        server.OnDisconnected += (reason, _) => disconnectTcs.TrySetResult(reason);

        await client.GoAwayAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Server must detect client shutdown (via GoAway frame or transport close)
        var reason = await disconnectTcs.Task.WaitAsync(cts.Token);
        Assert.True(reason is DisconnectReason.GoAwayReceived or DisconnectReason.TransportError);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_Idempotent()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.GoAwayAsync();
        await client.GoAwayAsync(); // second call should be no-op

        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
