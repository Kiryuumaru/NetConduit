using NetConduit.Interfaces;

namespace NetConduit.Transit.Stream.UnitTests;

public sealed class StreamTransitReadyReplayTests
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
    public async Task Ready_FiresWhenWriteOnlyTransitConstructedOverReadyChannel()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("sr>>");
        var serverRead = await server.AcceptChannelAsync("sr>>", CancellationToken.None);
        await Task.WhenAll(clientWrite.WaitForReadyAsync(), serverRead.WaitForReadyAsync());

        var transit = new StreamTransit(clientWrite);
        var readyFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transit.Ready += (_, _) => readyFired.TrySetResult();

        var won = await Task.WhenAny(readyFired.Task, Task.Delay(2000));
        Assert.Same(readyFired.Task, won);
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
    }

    [Fact]
    public async Task Ready_FiresWhenReadOnlyTransitConstructedOverReadyChannel()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("sr2>>");
        var serverRead = await server.AcceptChannelAsync("sr2>>", CancellationToken.None);
        await Task.WhenAll(clientWrite.WaitForReadyAsync(), serverRead.WaitForReadyAsync());

        var transit = new StreamTransit(serverRead);
        var readyFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transit.Ready += (_, _) => readyFired.TrySetResult();

        var won = await Task.WhenAny(readyFired.Task, Task.Delay(2000));
        Assert.Same(readyFired.Task, won);
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
    }
}
