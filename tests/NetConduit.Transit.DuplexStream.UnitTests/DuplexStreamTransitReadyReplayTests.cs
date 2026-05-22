using NetConduit.Interfaces;

namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitReadyReplayTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    [Fact]
    public async Task Ready_FiresWhenConstructedOverAlreadyReadyChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("rr>>");
        var serverRead = await server.AcceptChannelAsync("rr>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("rr<<");
        var clientRead = await client.AcceptChannelAsync("rr<<", CancellationToken.None);

        await Task.WhenAll(
            clientWrite.WaitForReadyAsync(),
            clientRead.WaitForReadyAsync(),
            serverWrite.WaitForReadyAsync(),
            serverRead.WaitForReadyAsync());

        // Both underlying channels have already raised their one-shot Ready event
        // BEFORE the transit is constructed. The transit must synthesize the
        // replay so a subscriber attached after construction still observes Ready.
        var transit = new DuplexStreamTransit(clientWrite, clientRead);

        var readyFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transit.Ready += (_, _) => readyFired.TrySetResult();

        var won = await Task.WhenAny(readyFired.Task, Task.Delay(2000));
        Assert.Same(readyFired.Task, won);
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
    }

    [Fact]
    public async Task Ready_FiresExactlyOnceWhenChannelsAlreadyReady()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("ro>>");
        var serverRead = await server.AcceptChannelAsync("ro>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("ro<<");
        var clientRead = await client.AcceptChannelAsync("ro<<", CancellationToken.None);

        await Task.WhenAll(
            clientWrite.WaitForReadyAsync(),
            clientRead.WaitForReadyAsync(),
            serverWrite.WaitForReadyAsync(),
            serverRead.WaitForReadyAsync());

        var transit = new DuplexStreamTransit(clientWrite, clientRead);

        int readyCount = 0;
        transit.Ready += (_, _) => Interlocked.Increment(ref readyCount);

        // Allow any straggler synchronous fire to be observed.
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref readyCount));

        await transit.DisposeAsync();
    }
}
