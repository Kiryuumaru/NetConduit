using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

// Issue #635 S/M/L benchmarks (ops-count + wall-time + JSON bytes).
// Conventions: S = small single-op change, M = typical object/array edit,
// L = over-1M-product input forcing the ArrayReplace gate. Correctness-only:
// every case also asserts the diff applies to the exact new state.
public sealed class DeltaDiffBenchmarks
{
    private static (JsonObject Old, JsonObject New) BuildProps(int count, int changed)
    {
        var oldObj = new JsonObject();
        var newObj = new JsonObject();
        for (int i = 0; i < count; i++)
        {
            oldObj[$"k{i}"] = i;
            newObj[$"k{i}"] = i < changed ? i + 1 : i;
        }
        return ((JsonObject)oldObj.DeepClone(), newObj);
    }

    private static void Report(string name, List<DeltaOperation> ops, long ms, JsonNode newState)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(newState.ToJsonString());
        Console.WriteLine($"[delta-bench] {name}: ops={ops.Count} wall={ms}ms bytes={bytes}");
    }

    [Fact]
    public void Bench_S_SingleOp()
    {
        var (oldState, newState) = BuildProps(10, 1);
        var sw = Stopwatch.StartNew();
        var ops = DeltaDiff.ComputeDelta(oldState, newState);
        sw.Stop();
        Assert.Single(ops);
        Report("S", ops, sw.ElapsedMilliseconds, newState);
        var staged = oldState.DeepClone();
        DeltaApply.ApplyDelta(staged, ops);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Fact]
    public void Bench_M_TypicalEdit()
    {
        var (oldState, newState) = BuildProps(1000, 10);
        var sw = Stopwatch.StartNew();
        var ops = DeltaDiff.ComputeDelta(oldState, newState);
        sw.Stop();
        Assert.Equal(10, ops.Count);
        Report("M", ops, sw.ElapsedMilliseconds, newState);
        var staged = oldState.DeepClone();
        DeltaApply.ApplyDelta(staged, ops);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }

    [Fact]
    public void Bench_L_Over1MProduct_Replace()
    {
        var oldArr = new JsonArray();
        var newArr = new JsonArray();
        for (int i = 0; i < 1200; i++) oldArr.Add(i);
        for (int i = 0; i < 1250; i++) newArr.Add(i + 1);
        var oldState = new JsonObject { ["items"] = oldArr };
        var newState = new JsonObject { ["items"] = newArr };
        var sw = Stopwatch.StartNew();
        var ops = DeltaDiff.ComputeDelta(oldState, newState);
        sw.Stop();
        Assert.Single(ops);
        Assert.Equal(DeltaOp.ArrayReplace, ops[0].Op);
        Report("L", ops, sw.ElapsedMilliseconds, newState);
        var staged = oldState.DeepClone();
        DeltaApply.ApplyDelta(staged, ops);
        Assert.Equal(newState.ToJsonString(), staged.ToJsonString());
    }
}
