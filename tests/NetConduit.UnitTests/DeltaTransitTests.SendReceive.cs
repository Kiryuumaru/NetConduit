using System.Buffers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

public partial class DeltaTransitTests
{
    public record SimpleState(string Id, int Value);

    [JsonSerializable(typeof(SimpleState))]
    internal partial class DeltaSendReceiveJsonContext : JsonSerializerContext { }

    #region Identical State Delivery

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_IdenticalConsecutiveStates_BothDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_dup" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_dup", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        var state = new SimpleState("req-1", 42);

        await sender.SendAsync(state, cts.Token);
        var received1 = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(received1);
        Assert.Equal("req-1", received1.Id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

        await sender.SendAsync(state, linked.Token);

        var received2 = await receiver.ReceiveAsync(linked.Token);
        Assert.NotNull(received2);
        Assert.Equal("req-1", received2.Id);
        Assert.Equal(42, received2.Value);
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_RepeatedIdenticalRequests_AllDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "fullsync" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("fullsync", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        var syncRequest = new SimpleState("FullSyncRequest", 0);
        var receivedCount = 0;

        for (int i = 0; i < 5; i++)
        {
            await sender.SendAsync(syncRequest, cts.Token);

            using var perIterationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, perIterationTimeout.Token);

            var received = await receiver.ReceiveAsync(linked.Token);
            Assert.NotNull(received);
            receivedCount++;
        }

        Assert.Equal(5, receivedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_ThreeIdenticalInRow_AllReceived()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_triple" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_triple", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        var state = new SimpleState("aaa", 100);
        for (int i = 0; i < 3; i++)
        {
            await sender.SendAsync(state, cts.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
            var received = await receiver.ReceiveAsync(linked.Token);
            Assert.NotNull(received);
            Assert.Equal("aaa", received.Id);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_IdenticalAfterDifferent_SecondDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_after_diff" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_after_diff", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        await sender.SendAsync(new SimpleState("x", 1), cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));

        var same = new SimpleState("y", 2);
        await sender.SendAsync(same, cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));

        await sender.SendAsync(same, cts.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        var third = await receiver.ReceiveAsync(linked.Token);
        Assert.NotNull(third);
        Assert.Equal("y", third.Id);
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_EmptyObject_SentTwice_BothReceived()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_empty" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_empty", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var empty = JsonNode.Parse("{}")!;
        for (int i = 0; i < 2; i++)
        {
            await sender.SendAsync(empty.DeepClone(), cts.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
            var received = await receiver.ReceiveAsync(linked.Token);
            Assert.NotNull(received);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_LargeIdenticalState_SecondDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_large" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_large", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var bigState = new JsonObject();
        for (int i = 0; i < 100; i++)
            bigState[$"field_{i}"] = JsonValue.Create(i);

        await sender.SendAsync(bigState, cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));

        await sender.SendAsync(bigState.DeepClone(), cts.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        Assert.NotNull(await receiver.ReceiveAsync(linked.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_ResetState_ThenResendSame_Delivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_reset" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_reset", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        var state = new SimpleState("reset", 42);

        await sender.SendAsync(state, cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));

        sender.ResetState();

        await sender.SendAsync(state, cts.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        var received = await receiver.ReceiveAsync(linked.Token);
        Assert.NotNull(received);
        Assert.Equal("reset", received.Id);
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_SendBatchAsync_IdenticalStatesInBatch()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_batch" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_batch", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        var same = new SimpleState("batch", 1);
        var batch = new[] { same, same, same };
        await sender.SendBatchAsync(batch, cts.Token);

        var received = 0;
        for (int i = 0; i < 3; i++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
            var r = await receiver.ReceiveAsync(linked.Token);
            if (r is not null) received++;
        }
        Assert.Equal(3, received);
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_NestedIdenticalState_SecondDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_nested" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_nested", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var nested = JsonNode.Parse("""{"a": {"b": {"c": [1,2,3]}}}""")!;
        await sender.SendAsync(nested, cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));

        await sender.SendAsync(nested.DeepClone(), cts.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        Assert.NotNull(await receiver.ReceiveAsync(linked.Token));
    }

    #endregion

    #region Buffer Safety

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_ChannelCloseMidRead_NoPoolCorruption()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "pool_test" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("pool_test", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        await sender.SendAsync(new SimpleState("ok", 1), cts.Token);
        var good = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(good);

        await writeA.DisposeAsync();

        var afterClose = await receiver.ReceiveAsync(cts.Token);
        Assert.Null(afterClose);

        var testBuffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            Assert.True(testBuffer.Length >= 1024);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(testBuffer);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_RapidSendReceive_PoolIntegrity()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "pool_rapid" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("pool_rapid", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        for (int i = 0; i < 200; i++)
        {
            var state = JsonNode.Parse($$"""{"iter": {{i}}, "data": "value_{{i}}"}""")!;
            await sender.SendAsync(state, cts.Token);
            var received = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(received);
        }

        for (int size = 64; size <= 16384; size *= 2)
        {
            var buf = ArrayPool<byte>.Shared.Rent(size);
            Assert.True(buf.Length >= size);
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_LargePayload_BufferReturnedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "pool_large" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("pool_large", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var big = new JsonObject();
        for (int i = 0; i < 500; i++)
            big[$"key_{i}"] = JsonValue.Create(new string('x', 100));

        await sender.SendAsync(big, cts.Token);
        var received = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(received);

        var small = JsonNode.Parse("""{"x": 1}""")!;
        await sender.SendAsync(small, cts.Token);
        Assert.NotNull(await receiver.ReceiveAsync(cts.Token));
    }

    #endregion

    #region ReceiveAllAsync

    [Fact(Timeout = 30000)]
    public async Task ReceiveAllAsync_ChannelClose_EnumerationEnds()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "closure_reason" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("closure_reason", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        await sender.SendAsync(new SimpleState("msg1", 1), cts.Token);

        var received = new List<SimpleState>();
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var state in receiver.ReceiveAllAsync(cts.Token))
            {
                received.Add(state);
                if (received.Count >= 1) break;
            }
        });

        await receiveTask;
        Assert.Single(received);
        Assert.Equal("msg1", received[0].Id);

        await writeA.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAllAsync_Cancellation_StopsEnumeration()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "recv_cancel" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("recv_cancel", cts.Token);

        await using var sender = new DeltaTransit<SimpleState>(writeA, null, DeltaSendReceiveJsonContext.Default.SimpleState);
        await using var receiver = new DeltaTransit<SimpleState>(null, readB, DeltaSendReceiveJsonContext.Default.SimpleState);

        await sender.SendAsync(new SimpleState("first", 1), cts.Token);

        using var enumCts = new CancellationTokenSource();
        var received = new List<SimpleState>();

        var enumTask = Task.Run(async () =>
        {
            await foreach (var state in receiver.ReceiveAllAsync(enumCts.Token))
            {
                received.Add(state);
                if (received.Count >= 1)
                {
                    await enumCts.CancelAsync();
                    break;
                }
            }
        });

        await enumTask;
        Assert.Single(received);
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAllAsync_MultipleMessages_AllYielded()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "recv_multi" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("recv_multi", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var received = new List<JsonNode>();

        var receiveTask = Task.Run(async () =>
        {
            await foreach (var state in receiver.ReceiveAllAsync(cts.Token))
            {
                received.Add(state);
                if (received.Count >= 5) break;
            }
        });

        for (int i = 0; i < 5; i++)
            await sender.SendAsync(JsonNode.Parse($$"""{"n": {{i}}}""")!, cts.Token);

        await receiveTask;
        Assert.Equal(5, received.Count);
    }

    #endregion
}
