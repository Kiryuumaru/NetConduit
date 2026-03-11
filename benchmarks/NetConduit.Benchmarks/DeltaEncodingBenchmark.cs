using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using NetConduit.Enums;
using NetConduit.Internal;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.Benchmarks;

/// <summary>
/// Benchmarks comparing delta encoding efficiency vs full state transmission.
/// Tests JSON vs binary encoding and measures compression ratios.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class DeltaEncodingBenchmark
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun
                .WithLaunchCount(1)
                .WithWarmupCount(2)
                .WithIterationCount(5));
            AddColumn(StatisticColumn.Mean);
            AddColumn(StatisticColumn.StdDev);
        }
    }

    private JsonObject _oldState = null!;
    private JsonObject _newState = null!;
    private List<DeltaOperation> _deltaOps = null!;
    private byte[] _binaryEncoded = null!;
    private string _jsonEncoded = null!;

    [Params(10, 50, 100)]
    public int PropertyCount { get; set; }

    [Params(1, 5, 20)]
    public int ChangedProperties { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create old state with PropertyCount properties
        _oldState = new JsonObject();
        for (int i = 0; i < PropertyCount; i++)
        {
            _oldState[$"property_{i}"] = $"value_{i}_original";
        }
        _oldState["nested"] = new JsonObject
        {
            ["level1"] = new JsonObject
            {
                ["level2"] = new JsonObject
                {
                    ["deepValue"] = "original"
                }
            }
        };

        // Create new state with ChangedProperties different values
        _newState = _oldState.DeepClone().AsObject();
        for (int i = 0; i < Math.Min(ChangedProperties, PropertyCount); i++)
        {
            _newState[$"property_{i}"] = $"value_{i}_modified";
        }
        _newState["nested"]!["level1"]!["level2"]!["deepValue"] = "modified";

        // Pre-compute delta operations
        _deltaOps = DeltaDiff.ComputeDelta(_oldState, _newState);

        // Pre-encode for decoding benchmarks
        _binaryEncoded = DeltaBinaryEncoder.Encode(_deltaOps);
        _jsonEncoded = DeltaTransit<JsonObject>.SerializeDelta(_deltaOps);
    }

    #region Diff Benchmarks

    [Benchmark(Description = "Compute Delta")]
    public List<DeltaOperation> ComputeDelta()
    {
        return DeltaDiff.ComputeDelta(_oldState, _newState);
    }

    [Benchmark(Description = "Apply Delta")]
    public JsonObject ApplyDelta()
    {
        var state = _oldState.DeepClone();
        DeltaApply.ApplyDelta(state, _deltaOps);
        return state.AsObject();
    }

    #endregion

    #region Encoding Benchmarks

    [Benchmark(Description = "Encode - JSON")]
    public string EncodeJson()
    {
        return DeltaTransit<JsonObject>.SerializeDelta(_deltaOps);
    }

    [Benchmark(Description = "Encode - Binary")]
    public byte[] EncodeBinary()
    {
        return DeltaBinaryEncoder.Encode(_deltaOps);
    }

    [Benchmark(Description = "Decode - JSON")]
    public List<DeltaOperation> DecodeJson()
    {
        return DeltaTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(_jsonEncoded));
    }

    [Benchmark(Description = "Decode - Binary")]
    public List<DeltaOperation> DecodeBinary()
    {
        return DeltaBinaryEncoder.Decode(_binaryEncoded);
    }

    #endregion

    #region Size Comparison Benchmarks

    [Benchmark(Description = "Full State (JSON bytes)")]
    public int FullStateSize()
    {
        return System.Text.Encoding.UTF8.GetByteCount(_newState.ToJsonString());
    }

    [Benchmark(Description = "Delta State (JSON bytes)")]
    public int DeltaJsonSize()
    {
        return System.Text.Encoding.UTF8.GetByteCount(_jsonEncoded);
    }

    [Benchmark(Description = "Delta State (Binary bytes)")]
    public int DeltaBinarySize()
    {
        return _binaryEncoded.Length;
    }

    #endregion
}
