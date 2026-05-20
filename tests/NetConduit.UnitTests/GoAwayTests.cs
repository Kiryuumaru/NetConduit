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
        server.Disconnected += (_, e) => disconnectTcs.TrySetResult(e.Reason);

        await client.GoAwayAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

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

    [Fact]
    public async Task GoAway_WaitsForOpenChannelsThenAbortsOnTimeout()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(500),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var writer = client.OpenChannel("draining");
        await server.AcceptChannelAsync("draining", CancellationToken.None);
        await writer.WaitForReadyAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.GoAwayAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"GoAwayAsync returned in {stopwatch.ElapsedMilliseconds} ms, but should have waited the drain timeout.");
        Assert.Equal(ChannelState.Closed, writer.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, writer.CloseReason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_ReturnsImmediatelyWhenChannelsAlreadyClosed()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(30),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.GoAwayAsync();
        stopwatch.Stop();

        // No channels open => drain returns immediately without waiting the 30 s timeout.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"GoAwayAsync with no open channels took {stopwatch.ElapsedMilliseconds} ms; should be near-zero.");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_RejectsNewOpenChannel()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.GoAwayAsync();

        Assert.Throws<InvalidOperationException>(() => client.OpenChannel("after-goaway"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
