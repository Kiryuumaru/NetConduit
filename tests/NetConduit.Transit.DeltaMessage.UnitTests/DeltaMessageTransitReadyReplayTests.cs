using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

[JsonSerializable(typeof(JsonObject))]
internal partial class ReadyReplayJsonContext : JsonSerializerContext { }

public sealed class DeltaMessageTransitReadyReplayTests
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
    public async Task Ready_FiresWhenConstructedOverReadyChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("dr>>");
        var serverRead = await server.AcceptChannelAsync("dr>>", CancellationToken.None);
        await Task.WhenAll(clientWrite.WaitForReadyAsync(), serverRead.WaitForReadyAsync());

        var transit = new DeltaMessageTransit<JsonObject>(
            clientWrite, serverRead, ReadyReplayJsonContext.Default.JsonObject);

        var readyFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transit.Ready += (_, _) => readyFired.TrySetResult();

        var won = await Task.WhenAny(readyFired.Task, Task.Delay(2000));
        Assert.Same(readyFired.Task, won);
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
    }

    [Fact]
    public async Task Ready_FiresExactlyOnce()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientWrite = client.OpenChannel("dr2>>");
        var serverRead = await server.AcceptChannelAsync("dr2>>", CancellationToken.None);
        await Task.WhenAll(clientWrite.WaitForReadyAsync(), serverRead.WaitForReadyAsync());

        var transit = new DeltaMessageTransit<JsonObject>(
            clientWrite, serverRead, ReadyReplayJsonContext.Default.JsonObject);

        var fires = 0;
        transit.Ready += (_, _) => Interlocked.Increment(ref fires);

        await Task.Delay(100);
        Assert.Equal(1, fires);

        await transit.DisposeAsync();
    }
}
