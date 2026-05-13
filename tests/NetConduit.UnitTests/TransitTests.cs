using System.Text.Json;
using System.Text.Json.Nodes;
using NetConduit.Internal;
using Xunit;

namespace NetConduit.UnitTests;

public sealed class DeltaDiffTests
{
    [Fact]
    public void IdenticalObjects_EmptyDelta()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice","age":30}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Empty(ops);
    }

    [Fact]
    public void PropertyChange_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice","age":31}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(new object[] { "age" }, ops[0].Path);
    }

    [Fact]
    public void PropertyAdded_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":"alice"}""");
        var b = JsonNode.Parse("""{"name":"alice","email":"alice@test.com"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Contains("email", ops[0].Path.Cast<string>());
    }

    [Fact]
    public void PropertyRemoved_ProducesRemoveOp()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Remove, ops[0].Op);
        Assert.Equal(new object[] { "age" }, ops[0].Path);
    }

    [Fact]
    public void NestedChange_ProducesNestedPath()
    {
        var a = JsonNode.Parse("""{"user":{"name":"alice","age":30}}""");
        var b = JsonNode.Parse("""{"user":{"name":"alice","age":31}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(2, ops[0].Path.Length);
    }

    [Fact]
    public void SetOp_ModifiesValue()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["age"], JsonValue.Create(31), null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        Assert.Equal(31, node!["age"]!.GetValue<int>());
    }

    [Fact]
    public void RemoveOp_RemovesProperty()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Remove, ["age"], null, null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        Assert.Null(node!["age"]);
    }

    [Fact]
    public void SetNull_SetsNull()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.SetNull, ["age"], null, null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        var obj = node!.AsObject();
        Assert.True(obj.ContainsKey("age"));
    }

    [Fact]
    public void ComputeAndApply_Roundtrip()
    {
        var original = JsonNode.Parse("""{"players":[{"id":1,"name":"alice","score":100},{"id":2,"name":"bob","score":200}],"status":"playing"}""");
        var modified = JsonNode.Parse("""{"players":[{"id":1,"name":"alice","score":150},{"id":2,"name":"bob","score":200}],"status":"paused"}""");

        var ops = DeltaDiff.ComputeDelta(original!, modified!);
        Assert.NotEmpty(ops);

        DeltaApply.ApplyDelta(original!, ops);

        Assert.Equal("paused", original!["status"]!.GetValue<string>());
        var players = original["players"]!.AsArray();
        Assert.Equal(150, players[0]!["score"]!.GetValue<int>());
    }

    [Fact]
    public void SerializeDelta_Roundtrips()
    {
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["name"], JsonValue.Create("bob"), null),
            new(DeltaOp.Remove, ["age"], null, null),
            new(DeltaOp.ArrayInsert, ["items"], JsonValue.Create("new-item"), 2),
        };

        var json = DeltaMessageTransit<JsonObject>.SerializeDelta(ops);
        var deserialized = DeltaMessageTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(3, deserialized.Count);

        Assert.Equal(DeltaOp.Set, deserialized[0].Op);
        Assert.Equal(new object[] { "name" }, deserialized[0].Path);

        Assert.Equal(DeltaOp.Remove, deserialized[1].Op);
        Assert.Equal(new object[] { "age" }, deserialized[1].Path);

        Assert.Equal(DeltaOp.ArrayInsert, deserialized[2].Op);
        Assert.Equal(2, deserialized[2].Index);
    }
}

public sealed class TransitTests
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

    #region StreamTransit

    [Fact]
    public async Task StreamTransit_WriteOnly_Sends()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s1"));
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanRead);

        byte[] data = [10, 20, 30];
        await transit.WriteAsync(data);

        var readChannel = await server.AcceptChannelAsync("s1", CancellationToken.None);
        var buf = new byte[10];
        int read = await readChannel.ReadAsync(buf, CancellationToken.None);
        Assert.Equal(3, read);
        Assert.Equal(data, buf[..3]);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadOnly_Receives()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("s2");
        var readChannel = await server.AcceptChannelAsync("s2", CancellationToken.None);
        var transit = new StreamTransit(readChannel);

        Assert.True(transit.CanRead);
        Assert.False(transit.CanWrite);

        byte[] data = [40, 50, 60, 70];
        await writeChannel.WriteAsync(data);

        var buf = new byte[10];
        int read = await transit.ReadAsync(buf);
        Assert.Equal(4, read);
        Assert.Equal(data, buf[..4]);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_WriteThrowsWhenReadOnly()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("s3");
        var readChannel = await server.AcceptChannelAsync("s3", CancellationToken.None);
        var transit = new StreamTransit(readChannel);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.WriteAsync(new byte[] { 1 }).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadThrowsWhenWriteOnly()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s4"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.ReadAsync(new byte[10]).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_SeekThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s5"));
        Assert.False(transit.CanSeek);
        Assert.Throws<NotSupportedException>(() => transit.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => transit.Length);
        Assert.Throws<NotSupportedException>(() => transit.Position);
        Assert.Throws<NotSupportedException>(() => transit.Position = 0);
        Assert.Throws<NotSupportedException>(() => transit.SetLength(0));
        transit.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region DuplexStreamTransit

    [Fact]
    public async Task DuplexStreamTransit_BidirectionalDataFlow()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("d1>>");
        var clientRead = await server.AcceptChannelAsync("d1>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("d1<<");
        var serverRead = await client.AcceptChannelAsync("d1<<", CancellationToken.None);

        var clientTransit = new DuplexStreamTransit(clientWrite, serverRead);
        var serverTransit = new DuplexStreamTransit(serverWrite, clientRead);

        await clientTransit.WriteAsync(new byte[] { 1, 2, 3 });
        var buf = new byte[10];
        int read = await serverTransit.ReadAsync(buf);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..3]);

        await serverTransit.WriteAsync(new byte[] { 4, 5 });
        read = await clientTransit.ReadAsync(buf);
        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 4, 5 }, buf[..2]);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_CanReadCanWrite()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("d2>>");
        var r = await server.AcceptChannelAsync("d2>>", CancellationToken.None);

        var transit = new DuplexStreamTransit(w, r);
        Assert.True(transit.CanRead);
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region MessageTransit

    [Fact]
    public async Task MessageTransit_SendReceive_RoundTrips()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("m1>>");
        var clientReadCh = await server.AcceptChannelAsync("m1>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("m1<<");
        var serverReadCh = await client.AcceptChannelAsync("m1<<", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var clientTransit = new MessageTransit<TestMessage, TestMessage>(clientWrite, serverReadCh);
        var serverTransit = new MessageTransit<TestMessage, TestMessage>(serverWrite, clientReadCh);
#pragma warning restore IL2026, IL3050

        var sent = new TestMessage { Name = "hello", Value = 42 };
        await clientTransit.SendAsync(sent);

        var received = await serverTransit.ReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal("hello", received.Name);
        Assert.Equal(42, received.Value);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAllAsync_StreamsMessages()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("m2>>");
        var clientReadCh = await server.AcceptChannelAsync("m2>>", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var sender = new MessageTransit<TestMessage, TestMessage>(clientWrite, null);
        var receiver = new MessageTransit<TestMessage, TestMessage>(null, clientReadCh);
#pragma warning restore IL2026, IL3050

        await sender.SendAsync(new TestMessage { Name = "a", Value = 1 });
        await sender.SendAsync(new TestMessage { Name = "b", Value = 2 });
        await sender.SendAsync(new TestMessage { Name = "c", Value = 3 });

        await sender.DisposeAsync();

        var messages = new List<TestMessage>();
        await foreach (var msg in receiver.ReceiveAllAsync())
        {
            messages.Add(msg);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal("a", messages[0].Name);
        Assert.Equal("b", messages[1].Name);
        Assert.Equal("c", messages[2].Name);

        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_SendThrowsWhenNoWriteChannel()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m3");
        var r = await server.AcceptChannelAsync("m3", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(null, r);
#pragma warning restore IL2026, IL3050

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.SendAsync(new TestMessage { Name = "x", Value = 0 }).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveThrowsWhenNoReadChannel()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m4");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, null);
#pragma warning restore IL2026, IL3050

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.ReceiveAsync().AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region TransitExtensions

    [Fact]
    public async Task TransitExtensions_OpenStream_CreatesWriteOnlyStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("e1");
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanRead);
        Assert.Equal("e1", transit.WriteChannelId);
        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransitExtensions_AcceptStream_CreatesReadOnlyStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("e2");
        var transit = await server.AcceptStreamAsync("e2");
        Assert.True(transit.CanRead);
        Assert.False(transit.CanWrite);
        Assert.Equal("e2", transit.ReadChannelId);
        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransitExtensions_OpenDuplexStream_CreatesFullDuplexStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var openTask = client.OpenDuplexStreamAsync("e3");
        var acceptTask = server.AcceptDuplexStreamAsync("e3");

        var clientStream = await openTask;
        var serverStream = await acceptTask;

        Assert.True(clientStream.CanRead);
        Assert.True(clientStream.CanWrite);
        Assert.Equal("e3>>", clientStream.WriteChannelId);
        Assert.Equal("e3<<", clientStream.ReadChannelId);

        Assert.True(serverStream.CanRead);
        Assert.True(serverStream.CanWrite);
        Assert.Equal("e3<<", serverStream.WriteChannelId);
        Assert.Equal("e3>>", serverStream.ReadChannelId);

        await clientStream.WriteAsync(new byte[] { 1, 2, 3 });
        var buf = new byte[10];
        int read = await serverStream.ReadAsync(buf);
        Assert.Equal(3, read);

        await serverStream.WriteAsync(new byte[] { 4, 5 });
        read = await clientStream.ReadAsync(buf);
        Assert.Equal(2, read);

        await clientStream.DisposeAsync();
        await serverStream.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region DeltaTransit End-to-End

    [Fact]
    public async Task DeltaTransit_FullState_Delta_Reset_Roundtrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        var clientWrite = client.OpenChannel("dt1");
        var clientReadCh = await server.AcceptChannelAsync("dt1", ct);

        var sender = new DeltaMessageTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaMessageTransit<JsonObject>(null, clientReadCh);

        // Full state send
        var state1 = new JsonObject { ["name"] = "alice", ["score"] = 100 };
        await sender.SendAsync(state1, ct);
        var received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("alice", received["name"]!.GetValue<string>());
        Assert.Equal(100, received["score"]!.GetValue<int>());

        // Delta send
        var state2 = new JsonObject { ["name"] = "alice", ["score"] = 150 };
        await sender.SendAsync(state2, ct);
        received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("alice", received["name"]!.GetValue<string>());
        Assert.Equal(150, received["score"]!.GetValue<int>());

        // Reset and full send again
        sender.ResetState();
        var state3 = new JsonObject { ["name"] = "bob", ["score"] = 200 };
        await sender.SendAsync(state3, ct);
        received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("bob", received["name"]!.GetValue<string>());
        Assert.Equal(200, received["score"]!.GetValue<int>());

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}

internal sealed class TestMessage
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

