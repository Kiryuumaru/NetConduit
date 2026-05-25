using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Models;
using NetConduit.Transit.DeltaMessage.Internal;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression tests for: DeltaMessageTransit.ReceiveAsync must apply deltas
/// atomically. A mid-batch failure must leave _lastReceivedState unchanged at the
/// receiver, then clear it and request a resync so subsequent deltas never apply
/// to a corrupt baseline.
/// </summary>
public sealed class DeltaTransitAtomicApplyTests
{
    [Fact]
    public void DeltaApply_ArrayInsert_IndexOutOfRange_ThrowsInvalidOperationException()
    {
        var root = JsonNode.Parse("""{"items":["a","b"]}""")!;
        var op = new DeltaOperation(DeltaOp.ArrayInsert, ["items"], JsonValue.Create("c"), 99);

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaApply.ApplyOperation(root, op));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeltaApply_ArrayRemove_IndexOutOfRange_ThrowsInvalidOperationException()
    {
        var root = JsonNode.Parse("""{"items":["a","b"]}""")!;
        var op = new DeltaOperation(DeltaOp.ArrayRemove, ["items"], null, 99);

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaApply.ApplyOperation(root, op));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeltaApply_RootArrayInsert_IndexOutOfRange_ThrowsInvalidOperationException()
    {
        var root = new JsonArray(1, 2);
        var op = new DeltaOperation(DeltaOp.ArrayInsert, [], JsonValue.Create(9), 99);

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaApply.ApplyOperation(root, op));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeltaApply_RootArrayRemove_IndexOutOfRange_ThrowsInvalidOperationException()
    {
        var root = new JsonArray(1, 2);
        var op = new DeltaOperation(DeltaOp.ArrayRemove, [], null, 99);

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaApply.ApplyOperation(root, op));
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_BadDelta_ThrowsInvalidOperationException_AndRequestsResync()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Sender side: client writes the (handcrafted) delta. Receiver side:
            // server reads, then server.write -> client.read for the resync request.
            var senderWrite = client.OpenChannel("dt-atomic-bad");
            var receiverRead = await server.AcceptChannelAsync("dt-atomic-bad", cts.Token);
            var receiverWrite = server.OpenChannel("dt-atomic-bad-back");
            var senderRead = await client.AcceptChannelAsync("dt-atomic-bad-back", cts.Token);

            await Task.WhenAll(
                senderWrite.WaitForReadyAsync(cts.Token),
                receiverRead.WaitForReadyAsync(cts.Token),
                receiverWrite.WaitForReadyAsync(cts.Token),
                senderRead.WaitForReadyAsync(cts.Token));

            // Receiver transit has BOTH channels so it can emit a resync frame on bad delta.
            var receiver = new DeltaMessageTransit<JsonObject>(receiverWrite, receiverRead);

            // Step 1: send a valid full state so receiver establishes _lastReceivedState.
            var fullJson = """{"items":["a","b"]}"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x00, fullJson, cts.Token);
            var s1 = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(s1);
            Assert.Equal(2, s1["items"]!.AsArray().Count);

            // Snapshot of "good" state — receiver should preserve this after the bad delta.
            var goodSnapshot = s1.ToJsonString();

            // Step 2: send a malformed delta. The batch contains:
            //   - a valid Set op (succeeds first)
            //   - an ArrayRemove with out-of-range index (throws mid-batch)
            // Pre-fix, the Set would mutate _lastReceivedState["x"] = 1 before the throw,
            // permanently corrupting the receiver baseline. Post-fix, the apply is staged
            // on a clone and the original state is preserved.
            // Delta payload format from DeltaMessageTransit.SerializeDelta:
            //   [ [opCode, [path], value?, index?]. ]
            //   Set = 0, ArrayRemove = 12 (see NetConduit.Enums.DeltaOp)
            var badDelta = """[[0,["x"],1],[12,["items"],99]]"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x01, badDelta, cts.Token);

            // Step 3: receiver must throw InvalidOperationException.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await receiver.ReceiveAsync(cts.Token));

            // Step 4: receiver must have emitted a resync request (0x02 frame) to the peer.
            var resyncFrame = await ReadFrameAsync(senderRead, cts.Token);
            Assert.Single(resyncFrame);
            Assert.Equal(0x02, resyncFrame[0]);

            // Step 5: send another full state. Receiver must accept and reflect the new
            // full state — proving its baseline was cleared (not stuck on corrupt state).
            var newFullJson = """{"items":["x","y","z"]}"""u8.ToArray();
            await WriteFrameAsync(senderWrite, 0x00, newFullJson, cts.Token);
            var s3 = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(s3);
            Assert.Equal(3, s3["items"]!.AsArray().Count);

            // Also confirm the pre-bad-delta state had no "x" property: if rollback failed
            // and the Set leaked, the original snapshot would have contained "x":1.
            Assert.DoesNotContain("\"x\"", goodSnapshot);

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

    private static async Task<byte[]> ReadFrameAsync(IReadChannel ch, CancellationToken ct)
    {
        var prefix = new byte[4];
        await ReadExactAsync(ch, prefix, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(prefix);
        var buf = new byte[len];
        await ReadExactAsync(ch, buf, ct);
        return buf;
    }

    private static async Task ReadExactAsync(IReadChannel ch, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await ch.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
