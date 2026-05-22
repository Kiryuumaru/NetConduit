using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

[JsonSerializable(typeof(JsonArray))]
internal partial class JsonArrayContext : JsonSerializerContext { }

/// <summary>
/// Regression tests for issue #241: DeltaMessageTransit&lt;JsonObject&gt; and
/// DeltaMessageTransit&lt;JsonArray&gt;.ReceiveAsync must not return a live
/// reference to the internal _lastReceivedState — caller mutation must not
/// silently corrupt internal state observed by subsequent delta-applied
/// receives.
/// </summary>
public sealed class DeltaMessageTransitJsonAliasingTests
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
    public async Task JsonObject_DeltaResult_MutationDoesNotContaminateInternalState()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenDeltaMessageTransitAsync("d-obj", JsonContext.Default.JsonObject);
        var serverTask = server.AcceptDeltaMessageTransitAsync("d-obj", JsonContext.Default.JsonObject);
        await using var sender = await clientTask;
        await using var receiver = await serverTask;

        // 1. Full state — receiver caches a clone in _lastReceivedState.
        await sender.SendAsync(new JsonObject { ["counter"] = 0, ["name"] = "alice" });
        _ = await receiver.ReceiveAsync();

        // 2. Delta — receiver mutates _lastReceivedState in place. With the bug,
        //    ReceiveAsync returns the live _lastReceivedState reference.
        await sender.SendAsync(new JsonObject { ["counter"] = 1, ["name"] = "alice" });
        var deltaResult = await receiver.ReceiveAsync();
        Assert.NotNull(deltaResult);
        Assert.Equal(1, deltaResult!["counter"]!.GetValue<int>());

        // Caller mutation. With the bug, this contaminates the receiver's
        // _lastReceivedState; with the fix, it touches only the returned clone.
        deltaResult["extra"] = "injected";
        deltaResult["counter"] = 99;

        // 3. Another delta — receiver applies it to _lastReceivedState. The
        //    delta only touches "counter".
        await sender.SendAsync(new JsonObject { ["counter"] = 2, ["name"] = "alice" });
        var got3 = await receiver.ReceiveAsync();
        Assert.NotNull(got3);
        Assert.Equal(2, got3!["counter"]!.GetValue<int>());
        Assert.Equal("alice", got3["name"]!.GetValue<string>());
        Assert.False(
            got3.ContainsKey("extra"),
            "Receiver _lastReceivedState was contaminated by caller mutation of an earlier delta result (#241).");
    }

    [Fact]
    public async Task JsonObject_DeltaResult_IsNotAliasedAcrossSubsequentReceives()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenDeltaMessageTransitAsync("d-obj-ref", JsonContext.Default.JsonObject);
        var serverTask = server.AcceptDeltaMessageTransitAsync("d-obj-ref", JsonContext.Default.JsonObject);
        await using var sender = await clientTask;
        await using var receiver = await serverTask;

        await sender.SendAsync(new JsonObject { ["v"] = 1 });
        _ = await receiver.ReceiveAsync();

        await sender.SendAsync(new JsonObject { ["v"] = 2 });
        var firstDelta = await receiver.ReceiveAsync();

        await sender.SendAsync(new JsonObject { ["v"] = 3 });
        var secondDelta = await receiver.ReceiveAsync();

        Assert.NotNull(firstDelta);
        Assert.NotNull(secondDelta);
        Assert.False(
            ReferenceEquals(firstDelta, secondDelta),
            "Consecutive delta ReceiveAsync results aliased to the same _lastReceivedState (#241).");
        Assert.Equal(2, firstDelta!["v"]!.GetValue<int>());
        Assert.Equal(3, secondDelta!["v"]!.GetValue<int>());
    }

    [Fact]
    public async Task JsonArray_DeltaResult_MutationDoesNotContaminateInternalState()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        var clientTask = client.OpenDeltaMessageTransitAsync("d-arr", JsonArrayContext.Default.JsonArray);
        var serverTask = server.AcceptDeltaMessageTransitAsync("d-arr", JsonArrayContext.Default.JsonArray);
        await using var sender = await clientTask;
        await using var receiver = await serverTask;

        // 1. Full state.
        await sender.SendAsync(new JsonArray(1, 2, 3));
        _ = await receiver.ReceiveAsync();

        // 2. Delta — append element.
        await sender.SendAsync(new JsonArray(1, 2, 3, 4));
        var deltaResult = await receiver.ReceiveAsync();
        Assert.NotNull(deltaResult);
        Assert.Equal(4, deltaResult!.Count);

        // Caller mutation; must not contaminate _lastReceivedState.
        deltaResult.Add(999);

        // 3. Another delta — append a different element.
        await sender.SendAsync(new JsonArray(1, 2, 3, 4, 5));
        var got3 = await receiver.ReceiveAsync();
        Assert.NotNull(got3);
        Assert.Equal(5, got3!.Count);
        Assert.Equal(5, got3[4]!.GetValue<int>());
        Assert.DoesNotContain(got3, n => n is not null && n.GetValue<int>() == 999);
    }
}
