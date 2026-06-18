using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression: when the final flush of <see cref="DeltaMessageTransit{T}.SendBatchAsync"/>
/// throws (cancellation, transport closure), the local <c>_lastSentState</c> must NOT be
/// advanced past what the peer actually received. Otherwise the next <see cref="DeltaMessageTransit{T}.SendAsync"/>
/// computes a delta against a corrupted baseline, silently desyncing the peer.
/// </summary>
public sealed class DeltaMessageTransitBatchRollbackTests
{
    [Fact]
    public async Task SendBatchAsync_FinalFlushThrows_NextSendUsesPreBatchBaseline()
    {
        var write = new RecordingWriteChannel();
        await using var transit = new DeltaMessageTransit<JsonObject>(
            writeChannel: write,
            readChannel: null,
            JsonContext.Default.JsonObject);

        // Establish baseline s0 on the peer.
        var s0 = JsonNode.Parse("""{"score":0,"name":"alice"}""")!.AsObject();
        await transit.SendAsync(s0);
        Assert.Single(write.Messages);
        Assert.Equal((byte)0x00, ParseFrame(write.Messages[0]).type);

        // Arm the channel to throw on the next write — the batch's final delta flush.
        write.ThrowOnNextWrite = true;

        var s1 = JsonNode.Parse("""{"score":1,"name":"alice"}""")!.AsObject();
        var s2 = JsonNode.Parse("""{"score":2,"name":"alice"}""")!.AsObject();
        var s3 = JsonNode.Parse("""{"score":3,"name":"alice"}""")!.AsObject();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await transit.SendBatchAsync(new[] { s1, s2, s3 }));

        // No frame was successfully written for the batch.
        Assert.Single(write.Messages);

        // Disarm and send a state equal to the pre-batch baseline. With a correct
        // _lastSentState (== s0), the diff is empty → SendFull (frame type 0x00).
        // Pre-fix _lastSentState was advanced to s3, so the diff is non-empty →
        // SendDelta (frame type 0x01) against a corrupted baseline.
        write.ThrowOnNextWrite = false;
        var s4 = JsonNode.Parse("""{"score":0,"name":"alice"}""")!.AsObject();
        await transit.SendAsync(s4);

        Assert.Equal(2, write.Messages.Count);
        var (type1, _) = ParseFrame(write.Messages[1]);
        Assert.Equal((byte)0x00, type1);
    }

    private static (byte type, byte[] payload) ParseFrame(byte[] frame)
    {
        var len = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, 4));
        var type = frame[4];
        var payload = frame.AsSpan(5, len - 1).ToArray();
        return (type, payload);
    }

    private sealed class RecordingWriteChannel : IWriteChannel
    {
        public List<byte[]> Messages { get; } = new();
        public bool ThrowOnNextWrite { get; set; }

        public string ChannelId => "test";
        public ChannelState State => ChannelState.Open;
        public bool IsReady => true;
        public bool IsConnected => true;
        public ChannelPriority Priority => ChannelPriority.Normal;
        public ChannelStats Stats { get; } = new ChannelStats();
        public ChannelCloseReason? CloseReason => null;
        public Exception? CloseException => null;

        public event EventHandler? Ready { add { } remove { } }
        public event EventHandler? Connected { add { } remove { } }
        public event EventHandler<DisconnectedEventArgs>? Disconnected { add { } remove { } }
        public event EventHandler<ChannelCloseEventArgs>? Closed { add { } remove { } }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (ThrowOnNextWrite)
            {
                ThrowOnNextWrite = false;
                throw new OperationCanceledException("simulated mid-batch flush failure");
            }
            Messages.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public Stream AsStream() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
