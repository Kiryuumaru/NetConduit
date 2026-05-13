using System.Text.Json.Serialization;
using NetConduit.Interfaces;

namespace NetConduit.Transit.Message.UnitTests;

public sealed record SmokeMessage(string Name, int Value);

[JsonSerializable(typeof(SmokeMessage))]
internal partial class SmokeContext : JsonSerializerContext { }

public sealed class MessageTransitSmokeTests
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
    public async Task OpenMessageTransit_RoundTrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenMessageTransitAsync("msg", SmokeContext.Default.SmokeMessage);
        var serverTask = server.AcceptMessageTransitAsync("msg", SmokeContext.Default.SmokeMessage);

        await using var c = await clientTask;
        await using var s = await serverTask;

        await c.SendAsync(new SmokeMessage("alice", 42));
        var received = await s.ReceiveAsync();
        Assert.Equal(new SmokeMessage("alice", 42), received);
    }
}
