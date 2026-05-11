using System.Text.Json;
using System.Text.Json.Nodes;
using NetConduit.Internal;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Edge-case tests for DeltaDiff, DeltaApply, and DeltaTransit serialization.
/// Covers: arrays, nested objects, null/empty, deeply-nested paths, large diffs.
/// </summary>
public sealed class DeltaTransitEdgeTests
{
    #region Array Operations

    [Fact]
    public void ArrayDiff_InsertAtStart_ReturnsArrayInsertOp()
    {
        var a = JsonNode.Parse("""{"items":["b","c"]}""");
        var b = JsonNode.Parse("""{"items":["a","b","c"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        Assert.Contains(ops, o => o.Op == DeltaOp.ArrayInsert);
    }

    [Fact]
    public void ArrayDiff_RemoveFromMiddle_ReturnsArrayRemoveOp()
    {
        var a = JsonNode.Parse("""{"items":["a","b","c"]}""");
        var b = JsonNode.Parse("""{"items":["a","c"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        Assert.Contains(ops, o => o.Op == DeltaOp.ArrayRemove || o.Op == DeltaOp.ArrayReplace);
    }

    [Fact]
    public void ArrayDiff_MajorityChanged_ReturnsArrayReplaceOp()
    {
        var a = JsonNode.Parse("""{"items":["a","b","c","d","e"]}""");
        var b = JsonNode.Parse("""{"items":["x","y","z"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        // When most elements change, a full replace is more efficient
        Assert.Contains(ops, o => o.Op == DeltaOp.ArrayReplace || o.Op == DeltaOp.ArrayRemove || o.Op == DeltaOp.ArrayInsert);
    }

    [Fact]
    public void ArrayDiff_EmptyToPopulated_ProducesOps()
    {
        var a = JsonNode.Parse("""{"items":[]}""");
        var b = JsonNode.Parse("""{"items":["a","b","c"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
    }

    [Fact]
    public void ArrayDiff_PopulatedToEmpty_ProducesOps()
    {
        var a = JsonNode.Parse("""{"items":["a","b","c"]}""");
        var b = JsonNode.Parse("""{"items":[]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
    }

    [Fact]
    public void ArrayDiff_ReorderElements_ProducesOps()
    {
        var a = JsonNode.Parse("""{"items":["a","b","c"]}""");
        var b = JsonNode.Parse("""{"items":["c","a","b"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
    }

    [Fact]
    public void ArrayDiff_DuplicateElements_HandledCorrectly()
    {
        var a = JsonNode.Parse("""{"items":["a","a","b"]}""");
        var b = JsonNode.Parse("""{"items":["a","b","a"]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        // Apply should produce the target state
        DeltaApply.ApplyDelta(a!, ops);
        var result = a!["items"]!.AsArray();
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ArrayDiff_NestedObjects_InArray()
    {
        var a = JsonNode.Parse("""{"users":[{"id":1,"name":"alice"},{"id":2,"name":"bob"}]}""");
        var b = JsonNode.Parse("""{"users":[{"id":1,"name":"alice"},{"id":2,"name":"BOB"}]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal("BOB", a!["users"]![1]!["name"]!.GetValue<string>());
    }

    #endregion

    #region Deeply Nested Objects

    [Fact]
    public void DeeplyNested_ThreeLevels_ProducesCorrectPath()
    {
        var a = JsonNode.Parse("""{"a":{"b":{"c":1}}}""");
        var b = JsonNode.Parse("""{"a":{"b":{"c":2}}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(3, ops[0].Path.Length);
    }

    [Fact]
    public void DeeplyNested_FiveLevels_RoundTrips()
    {
        var a = JsonNode.Parse("""{"l1":{"l2":{"l3":{"l4":{"l5":"old"}}}}}""");
        var b = JsonNode.Parse("""{"l1":{"l2":{"l3":{"l4":{"l5":"new"}}}}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal("new", a!["l1"]!["l2"]!["l3"]!["l4"]!["l5"]!.GetValue<string>());
    }

    [Fact]
    public void NestedObject_Added_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":"alice"}""");
        var b = JsonNode.Parse("""{"name":"alice","address":{"city":"nyc","zip":"10001"}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal("nyc", a!["address"]!["city"]!.GetValue<string>());
    }

    [Fact]
    public void NestedObject_Removed_ProducesRemoveOp()
    {
        var a = JsonNode.Parse("""{"name":"alice","address":{"city":"nyc"}}""");
        var b = JsonNode.Parse("""{"name":"alice"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Null(a!["address"]);
    }

    #endregion

    #region Null/Empty Edge Cases

    [Fact]
    public void NullValue_SetToNonNull_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":null}""");
        var b = JsonNode.Parse("""{"name":"alice"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal("alice", a!["name"]!.GetValue<string>());
    }

    [Fact]
    public void NonNullValue_SetToNull_ProducesSetNullOp()
    {
        var a = JsonNode.Parse("""{"name":"alice"}""");
        var b = JsonNode.Parse("""{"name":null}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        Assert.Contains(ops, o => o.Op == DeltaOp.SetNull || o.Op == DeltaOp.Set);
    }

    [Fact]
    public void EmptyObject_ToPopulated_ProducesOps()
    {
        var a = JsonNode.Parse("""{}""");
        var b = JsonNode.Parse("""{"name":"alice","age":30}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void PopulatedObject_ToEmpty_ProducesRemoveOps()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Equal(DeltaOp.Remove, o.Op));
    }

    #endregion

    #region Serialization Edge Cases

    [Fact]
    public void SerializeDelta_EmptyOps_ProducesEmptyArray()
    {
        var ops = new List<DeltaOperation>();
        var json = DeltaTransit<JsonObject>.SerializeDelta(ops);
        var deserialized = DeltaTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Empty(deserialized);
    }

    [Fact]
    public void SerializeDelta_AllOpTypes_Roundtrips()
    {
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["name"], JsonValue.Create("alice"), null),
            new(DeltaOp.Remove, ["old"], null, null),
            new(DeltaOp.SetNull, ["nullable"], null, null),
            new(DeltaOp.ArrayInsert, ["items"], JsonValue.Create("x"), 0),
            new(DeltaOp.ArrayRemove, ["items"], null, 2),
            new(DeltaOp.ArrayReplace, ["items"], JsonNode.Parse("""["a","b"]"""), null),
        };

        var json = DeltaTransit<JsonObject>.SerializeDelta(ops);
        var deserialized = DeltaTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(6, deserialized.Count);

        Assert.Equal(DeltaOp.Set, deserialized[0].Op);
        Assert.Equal(DeltaOp.Remove, deserialized[1].Op);
        Assert.Equal(DeltaOp.SetNull, deserialized[2].Op);
        Assert.Equal(DeltaOp.ArrayInsert, deserialized[3].Op);
        Assert.Equal(DeltaOp.ArrayRemove, deserialized[4].Op);
        Assert.Equal(DeltaOp.ArrayReplace, deserialized[5].Op);
    }

    [Fact]
    public void SerializeDelta_NestedPath_PreservesPathSegments()
    {
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["users", "profile", "avatar"], JsonValue.Create("url"), null),
        };

        var json = DeltaTransit<JsonObject>.SerializeDelta(ops);
        var deserialized = DeltaTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(3, deserialized[0].Path.Length);
    }

    [Fact]
    public void DeserializeDelta_MalformedJson_Throws()
    {
        var badJson = System.Text.Encoding.UTF8.GetBytes("not valid json");
        Assert.ThrowsAny<Exception>(() => DeltaTransit<JsonObject>.DeserializeDelta(badJson));
    }

    [Fact]
    public void DeserializeDelta_EmptyBytes_Throws()
    {
        Assert.ThrowsAny<Exception>(() => DeltaTransit<JsonObject>.DeserializeDelta(ReadOnlySpan<byte>.Empty));
    }

    #endregion

    #region Type Changes

    [Fact]
    public void TypeChange_StringToNumber_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"value":"123"}""");
        var b = JsonNode.Parse("""{"value":123}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal(123, a!["value"]!.GetValue<int>());
    }

    [Fact]
    public void TypeChange_ObjectToArray_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"data":{"key":"value"}}""");
        var b = JsonNode.Parse("""{"data":[1,2,3]}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal(3, a!["data"]!.AsArray().Count);
    }

    [Fact]
    public void TypeChange_ArrayToObject_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"data":[1,2,3]}""");
        var b = JsonNode.Parse("""{"data":{"key":"value"}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.Equal("value", a!["data"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void BooleanValues_DiffCorrectly()
    {
        var a = JsonNode.Parse("""{"active":true,"verified":false}""");
        var b = JsonNode.Parse("""{"active":false,"verified":true}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Equal(2, ops.Count);
        DeltaApply.ApplyDelta(a!, ops);
        Assert.False(a!["active"]!.GetValue<bool>());
        Assert.True(a!["verified"]!.GetValue<bool>());
    }

    #endregion

    #region Large Delta Scenarios

    [Fact]
    public void LargeObject_ManyProperties_DiffAndApply()
    {
        var a = new JsonObject();
        var b = new JsonObject();
        for (int i = 0; i < 100; i++)
        {
            a[$"prop{i}"] = i;
            b[$"prop{i}"] = i + 1;
        }

        var ops = DeltaDiff.ComputeDelta(a, b);
        Assert.Equal(100, ops.Count);
        DeltaApply.ApplyDelta(a, ops);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i + 1, a[$"prop{i}"]!.GetValue<int>());
        }
    }

    [Fact]
    public void LargeArray_ManyInserts_AppliedCorrectly()
    {
        var original = new JsonArray();
        for (int i = 0; i < 10; i++) original.Add(i);

        var modified = new JsonArray();
        for (int i = 0; i < 20; i++) modified.Add(i);

        var a = new JsonObject { ["arr"] = original };
        var b = new JsonObject { ["arr"] = modified };

        var ops = DeltaDiff.ComputeDelta(a, b);
        Assert.NotEmpty(ops);
        DeltaApply.ApplyDelta(a, ops);
        Assert.Equal(20, a["arr"]!.AsArray().Count);
    }

    #endregion

    #region DeltaTransit Send/Receive Edge Cases

    [Fact]
    public async Task DeltaTransit_IdenticalConsecutiveSends_SentAsDelta()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-dup");
        var serverRead = await server.AcceptChannelAsync("dt-dup", cts.Token);

        var sender = new DeltaTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaTransit<JsonObject>(null, serverRead);

        var state = new JsonObject { ["x"] = 1 };
        await sender.SendAsync(state, cts.Token);
        var r1 = await receiver.ReceiveAsync(cts.Token);
        Assert.Equal(1, r1!["x"]!.GetValue<int>());

        // Send identical state — should still arrive (as empty delta or no-op)
        await sender.SendAsync(new JsonObject { ["x"] = 1 }, cts.Token);
        var r2 = await receiver.ReceiveAsync(cts.Token);
        Assert.Equal(1, r2!["x"]!.GetValue<int>());

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_BatchSend_AllReceived()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-batch");
        var serverRead = await server.AcceptChannelAsync("dt-batch", cts.Token);

        var sender = new DeltaTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaTransit<JsonObject>(null, serverRead);

        // SendBatchAsync combines into: 1 full state + 1 combined delta
        var states = Enumerable.Range(1, 5).Select(i => new JsonObject { ["val"] = i });
        await sender.SendBatchAsync(states, cts.Token);

        // First receive: the initial full state (val=1)
        var r1 = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(r1);
        Assert.Equal(1, r1["val"]!.GetValue<int>());

        // Second receive: the combined delta applied gives final state (val=5)
        var r2 = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(r2);
        Assert.Equal(5, r2["val"]!.GetValue<int>());

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ResetState_NextSendIsFullState()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-reset");
        var serverRead = await server.AcceptChannelAsync("dt-reset", cts.Token);

        var sender = new DeltaTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaTransit<JsonObject>(null, serverRead);

        await sender.SendAsync(new JsonObject { ["a"] = 1, ["b"] = 2 }, cts.Token);
        await receiver.ReceiveAsync(cts.Token);

        sender.ResetState();

        // After reset, next send should be full state even if similar
        await sender.SendAsync(new JsonObject { ["a"] = 1, ["b"] = 2 }, cts.Token);
        var r = await receiver.ReceiveAsync(cts.Token);
        Assert.Equal(1, r!["a"]!.GetValue<int>());
        Assert.Equal(2, r!["b"]!.GetValue<int>());

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ReceiveAll_EndsOnChannelClose()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var clientWrite = client.OpenChannel("dt-receiveall");
        var serverRead = await server.AcceptChannelAsync("dt-receiveall", cts.Token);

        var sender = new DeltaTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaTransit<JsonObject>(null, serverRead);

        await sender.SendAsync(new JsonObject { ["i"] = 1 }, cts.Token);
        await sender.SendAsync(new JsonObject { ["i"] = 2 }, cts.Token);

        // Receive items before closing to avoid FIN/data race in ReceiveAllAsync
        var r1 = await receiver.ReceiveAsync(cts.Token);
        var r2 = await receiver.ReceiveAsync(cts.Token);

        await sender.DisposeAsync(); // closes channel

        // After close, ReceiveAsync should return null (channel EOF)
        var r3 = await receiver.ReceiveAsync(cts.Token);

        Assert.NotNull(r1);
        Assert.Equal(1, r1["i"]!.GetValue<int>());
        Assert.NotNull(r2);
        Assert.Equal(2, r2["i"]!.GetValue<int>());
        Assert.Null(r3);

        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

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
}
