using System.Text;
using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

// Issue #635: bounded diff/batch work must stay correct. The Hirschberg LCS,
// id-indexed matcher, pair budget, and 512-op batch flush all preserve the
// observable contract: diff output applies cleanly to the new state, and
// large inputs fall back to ArrayReplace instead of quadratic blowup.
public sealed class DeltaDiffBoundedTests
{
    private static JsonNode Apply(JsonNode oldState, JsonNode newState)
    {
        var ops = DeltaDiff.ComputeDelta(oldState, newState);
        var staged = oldState.DeepClone();
        DeltaApply.ApplyDelta(staged, ops);
        return staged;
    }

    [Fact]
    public void Lcs_LargePrimitiveArrays_MatchesQuadraticResult()
    {
        // 900 elements each, one insertion + one deletion. Product 810k < 1M
        // gate, so both paths run the LCS (not ArrayReplace) and must agree.
        var oldArr = new JsonArray();
        var newArr = new JsonArray();
        for (int i = 0; i < 900; i++) oldArr.Add(i);
        for (int i = 0; i < 900; i++)
        {
            if (i == 450) newArr.Add(99999);
            if (i == 700) continue;
            newArr.Add(i);
        }
        var lcs = DeltaDiff.ComputeLCS(oldArr, newArr);
        Assert.Equal(899, lcs.Count);

        var oldState = new JsonObject { ["items"] = oldArr };
        var newState = new JsonObject { ["items"] = (JsonNode)newArr.DeepClone() };
        var staged = Apply(oldState, newState);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Fact]
    public void Diff_Over1MProduct_FallsBackToArrayReplace()
    {
        // 1200 x 1200 = 1.44M > 1M gate → single ArrayReplace, not 1200 Sets.
        // NOTE: same-length arrays take the DiffArraysSameLength fast path
        // (per-index Sets, no LCS), so lengths must differ to reach the gate.
        var oldArr = new JsonArray();
        var newArr = new JsonArray();
        for (int i = 0; i < 1200; i++) oldArr.Add(i);
        for (int i = 0; i < 1250; i++) newArr.Add(i + 1);
        var oldState = new JsonObject { ["items"] = oldArr };
        var newState = new JsonObject { ["items"] = (JsonNode)newArr.DeepClone() };

        var ops = DeltaDiff.ComputeDelta(oldState, newState);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.ArrayReplace, ops[0].Op);

        var staged = oldState.DeepClone();
        DeltaApply.ApplyDelta(staged, ops);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Fact]
    public void Diff_IdIndexedMatcher_ReorderFallsBackToReplace()
    {
        // Direct unit check of the reorder guard: pairs (1,0),(0,1) are
        // out of order by construction. (End-to-end rotation diffs take
        // other code paths — per-field Sets under the op-count fallback —
        // so the guard itself is pinned here, and equivalence is pinned
        // by the randomized tests below.)
        var pairs = new List<(int oldIdx, int newIdx)> { (1, 0), (0, 1) };
        Assert.True(DeltaDiff.RequiresReorderFallback(pairs));
        Assert.False(DeltaDiff.RequiresReorderFallback([(0, 0), (1, 1)]));
    }

    [Fact]
    public void Diff_IdIndexedMatcher_MatchWithoutReorder_AppliesCleanly()
    {
        var oldState = JsonNode.Parse("""{"rows":[{"id":1,"v":"a"},{"id":2,"v":"b"}]}""")!;
        var newState = JsonNode.Parse("""{"rows":[{"id":1,"v":"a2"},{"id":2,"v":"b"},{"id":3,"v":"c"}]}""")!;
        var staged = Apply(oldState, newState);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void Diff_RandomizedObjectArrays_Equivalence(int seed)
    {
        // Deterministic seeded corpus: diff output must always apply to the
        // exact new state regardless of matcher path taken.
        var rng = new Random(seed);
        var oldRows = new JsonArray();
        var newRows = new JsonArray();
        for (int i = 0; i < 60; i++)
            oldRows.Add(JsonNode.Parse($"{{\"id\":{i},\"v\":\"{rng.Next(1000)}\"}}"));
        for (int i = 0; i < 60; i++)
        {
            if (rng.NextDouble() < 0.1) continue;              // deletion
            if (rng.NextDouble() < 0.1)                        // insertion
                newRows.Add(JsonNode.Parse($"{{\"id\":{1000 + i},\"v\":\"new\"}}"));
            newRows.Add(JsonNode.Parse($"{{\"id\":{i},\"v\":\"{rng.Next(1000)}\"}}"));
        }
        var oldState = new JsonObject { ["rows"] = oldRows };
        var newState = new JsonObject { ["rows"] = newRows };
        var staged = Apply(oldState, newState);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Fact(Timeout = 60000)]
    public async Task SendBatch_LargeBatch_FlushesMidBatch_AndRoundTrips()
    {
        // 600 states each adding one key: combined ops would reach 600
        // without the 512 flush. Receiver must converge to the final state.
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var senderWrite = client.OpenChannel("dt-batch-flush");
            var receiverRead = await server.AcceptChannelAsync("dt-batch-flush", cts.Token);
            await Task.WhenAll(
                senderWrite.WaitForReadyAsync(cts.Token),
                receiverRead.WaitForReadyAsync(cts.Token));

            var sender = new DeltaMessageTransit<JsonObject>(senderWrite, null);
            var receiver = new DeltaMessageTransit<JsonObject>(null, receiverRead);

            var states = new List<JsonObject>();
            for (int i = 0; i < 600; i++)
            {
                var s = new JsonObject();
                for (int k = 0; k <= i; k++) s[$"k{k}"] = k;
                states.Add(s);
            }
            await sender.SendBatchAsync(states, cts.Token);

            JsonObject? last = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await foreach (var s in receiver.ReceiveAllAsync(timeout.Token))
            {
                last = s;
                if (last is not null && last.Count == 600) break;
            }
            Assert.NotNull(last);
            Assert.Equal(600, last!.Count);
            Assert.Equal(599, last["k599"]!.GetValue<int>());

            await sender.DisposeAsync();
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
}
