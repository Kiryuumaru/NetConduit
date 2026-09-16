using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetConduit.Interfaces;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

// Issue #611: a hostile delta path segment (boolean, null, float, object)
// must fail fast with JsonException at deserialization time — before the
// delta ever reaches DeltaApply — so one malformed token cannot force a
// full-state resync round-trip per delta.
public sealed class DeltaHostilePathTests
{
    [Theory]
    [InlineData("""[[0,[true],1]]""")]       // boolean segment
    [InlineData("""[[0,[null],1]]""")]       // null segment
    [InlineData("""[[0,["a",1.5],1]]""")]    // float segment
    [InlineData("""[[0,["a",{}],1]]""")]     // object segment
    [InlineData("""[[0,["a",[1]],1]]""")]    // nested-array segment
    public void DeserializeDelta_HostileSegment_ThrowsJsonException(string payload)
    {
        var ex = Assert.Throws<JsonException>(() =>
            DeltaMessageTransit<JsonObject>.DeserializeDelta(Encoding.UTF8.GetBytes(payload)));
        Assert.Contains("must be a string or integer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DeserializeDelta_HostileSegment_MessageNamesOpAndElement(int hostileOp)
    {
        var good = """[0,["ok"],1]""";
        var bad = """[0,[true],3]""";
        var payload = hostileOp == 0
            ? "[" + bad + "]"
            : "[" + good + "," + good + "," + bad + "]";
        var ex = Assert.Throws<JsonException>(() =>
            DeltaMessageTransit<JsonObject>.DeserializeDelta(Encoding.UTF8.GetBytes(payload)));
        Assert.Contains($"Delta op {hostileOp} path segment at index 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeDelta_ValidSegments_RoundTripUnchanged()
    {
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["name", 0, "nested", 42], JsonValue.Create("v"), null),
        };
        var json = DeltaMessageTransit<JsonObject>.SerializeDelta(ops);
        var back = DeltaMessageTransit<JsonObject>.DeserializeDelta(Encoding.UTF8.GetBytes(json));
        Assert.Single(back);
        Assert.Equal(new object[] { "name", 0, "nested", 42 }, back[0].Path);
    }

    [Fact]
    public void DeserializeDelta_EmptyPath_StaysLegal()
    {
        var payload = """[[14,[],[]]]"""; // ArrayReplace = 14, empty path = root
        var ops = DeltaMessageTransit<JsonObject>.DeserializeDelta(Encoding.UTF8.GetBytes(payload));
        Assert.Single(ops);
        Assert.Empty(ops[0].Path);
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_HostilePath_ThrowsJsonException_AndRequestsNoResync()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var senderWrite = client.OpenChannel("dt-hostile-path");
            var receiverRead = await server.AcceptChannelAsync("dt-hostile-path", cts.Token);
            var receiverWrite = server.OpenChannel("dt-hostile-path-back");
            var senderRead = await client.AcceptChannelAsync("dt-hostile-path-back", cts.Token);

            await Task.WhenAll(
                senderWrite.WaitForReadyAsync(cts.Token),
                receiverRead.WaitForReadyAsync(cts.Token),
                receiverWrite.WaitForReadyAsync(cts.Token),
                senderRead.WaitForReadyAsync(cts.Token));

            var receiver = new DeltaMessageTransit<JsonObject>(receiverWrite, receiverRead);

            var fullJson = """{"items":["a","b"]}"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x00, fullJson, cts.Token);
            var s1 = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(s1);

            // Hostile delta: boolean path segment. Must throw JsonException
            // (parse failure), NOT InvalidOperationException (apply failure).
            var hostile = """[[0,[true],1]]"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x01, hostile, cts.Token);
            await Assert.ThrowsAsync<JsonException>(async () =>
                await receiver.ReceiveAsync(cts.Token));

            // No resync request may be emitted for a malformed payload: the
            // receiver keeps its baseline and stays silent on the back channel.
            var resyncCheck = senderRead.ReadAsync(new byte[5].AsMemory(), cts.Token).AsTask();
            var completed = await Task.WhenAny(resyncCheck, Task.Delay(TimeSpan.FromMilliseconds(300), cts.Token));
            Assert.NotEqual(resyncCheck, completed);

            // Receiver still serves the next full state on the intact baseline.
            var newFullJson = """{"items":["x","y"]}"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x00, newFullJson, cts.Token);
            var s3 = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(s3);
            Assert.Equal(2, s3["items"]!.AsArray().Count);

            await receiver.DisposeAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

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

    private static async Task WriteFrameAsync(IWriteChannel ch, byte messageType, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), 1 + payload.Length);
        frame[4] = messageType;
        Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        await ch.WriteAsync(frame, ct);
    }
}
