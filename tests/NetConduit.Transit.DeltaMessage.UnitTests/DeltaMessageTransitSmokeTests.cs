using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

[JsonSerializable(typeof(JsonObject))]
internal partial class JsonContext : JsonSerializerContext { }

public sealed class DeltaMessageTransitSmokeTests
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
    public async Task OpenDeltaMessageTransit_RoundTrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenDeltaMessageTransitAsync("delta", JsonContext.Default.JsonObject);
        var serverTask = server.AcceptDeltaMessageTransitAsync("delta", JsonContext.Default.JsonObject);

        await using var c = await clientTask;
        await using var s = await serverTask;

        var initial = JsonNode.Parse("""{"score":0,"name":"alice"}""")!.AsObject();
        await c.SendAsync(initial);
        var received = await s.ReceiveAsync();
        Assert.Equal(0, received!["score"]!.GetValue<int>());
        Assert.Equal("alice", received["name"]!.GetValue<string>());
    }
}
