using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

// Regression for #241. FromJsonNode pre-fix used .AsObject() / .AsArray() to
// produce T for T = JsonObject / T = JsonArray. Both are reference downcasts:
// the returned T is the same instance the transit holds in _lastReceivedState
// and continues to mutate via DeltaApply.ApplyDelta on every subsequent
// receive. This is detectable by reflection — the returned reference is
// observably identical to the field value. Post-fix the receive path produces
// a fresh DeepClone before downcasting, so the two diverge.
public sealed class Issue241RegressionTests
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
    public async Task ReceiveAsync_JsonObject_ReturnedRefIsNotAliasOfLastReceivedState()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-241r-obj");
        var serverRead = await server.AcceptChannelAsync("dt-241r-obj", cts.Token);

        var sender = new DeltaMessageTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaMessageTransit<JsonObject>(null, serverRead);

        // Full state. The 0x00 receive path parses a fresh JsonNode and clones
        // it into _lastReceivedState, so this case does not exercise the bug
        // — it does ensure _lastReceivedState is populated.
        await sender.SendAsync(new JsonObject { ["k"] = 1 }, cts.Token);
        var initial = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(initial);

        // Delta. The 0x01 receive path mutates _lastReceivedState in place and
        // pre-fix returns it by reference via .AsObject().
        await sender.SendAsync(new JsonObject { ["k"] = 2 }, cts.Token);
        var returned = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(returned);

        var internalRef = GetLastReceivedState(receiver);
        Assert.NotNull(internalRef);

        // The caller-visible reference MUST be a distinct instance from the
        // transit's internal mutable state. Pre-fix they are identical.
        Assert.False(ReferenceEquals(returned, internalRef),
            "DeltaMessageTransit<JsonObject>.ReceiveAsync returned the live _lastReceivedState by reference (#241).");

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveAsync_JsonArray_ReturnedRefIsNotAliasOfLastReceivedState()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-241r-arr");
        var serverRead = await server.AcceptChannelAsync("dt-241r-arr", cts.Token);

        var sender = new DeltaMessageTransit<JsonArray>(clientWrite, null);
        var receiver = new DeltaMessageTransit<JsonArray>(null, serverRead);

        await sender.SendAsync(new JsonArray { 1, 2, 3 }, cts.Token);
        var initial = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(initial);

        await sender.SendAsync(new JsonArray { 1, 2, 3, 4 }, cts.Token);
        var returned = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(returned);

        var internalRef = GetLastReceivedState(receiver);
        Assert.NotNull(internalRef);

        Assert.False(ReferenceEquals(returned, internalRef),
            "DeltaMessageTransit<JsonArray>.ReceiveAsync returned the live _lastReceivedState by reference (#241).");

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private static JsonNode? GetLastReceivedState<T>(DeltaMessageTransit<T> transit) where T : class
    {
        var field = typeof(DeltaMessageTransit<T>)
            .GetField("_lastReceivedState", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (JsonNode?)field.GetValue(transit);
    }
}
