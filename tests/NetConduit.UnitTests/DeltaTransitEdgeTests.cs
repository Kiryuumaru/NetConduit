using NetConduit.Enums;
using System.Text.Json.Nodes;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Edge case tests for DeltaDiff/DeltaApply that are not covered by existing tests:
/// - Type transitions (string→number, object→array, etc.)
/// - Deeply nested changes (5+ levels)
/// - Boolean/numeric edge values
/// - Empty string vs null distinction
/// - Array of objects mutations
/// </summary>
public class DeltaTransitEdgeTests
{
    #region Type Transitions

    [Fact]
    public void Diff_PropertyTypeChange_StringToNumber_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": "hello"}""")!;
        var updated = JsonNode.Parse("""{"val": 42}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(42, ops[0].Value?.GetValue<int>());
    }

    [Fact]
    public void Diff_PropertyTypeChange_NumberToString_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": 42}""")!;
        var updated = JsonNode.Parse("""{"val": "hello"}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal("hello", ops[0].Value?.GetValue<string>());
    }

    [Fact]
    public void Diff_PropertyTypeChange_ObjectToArray_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": {"a": 1}}""")!;
        var updated = JsonNode.Parse("""{"val": [1, 2, 3]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
    }

    [Fact]
    public void Diff_PropertyTypeChange_ArrayToObject_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": [1, 2]}""")!;
        var updated = JsonNode.Parse("""{"val": {"x": 1}}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
    }

    [Fact]
    public void Diff_PropertyTypeChange_BoolToString_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": true}""")!;
        var updated = JsonNode.Parse("""{"val": "true"}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
    }

    [Fact]
    public void RoundTrip_TypeChange_ProducesSameState()
    {
        var old = JsonNode.Parse("""{"a": "text", "b": 100, "c": true}""")!;
        var updated = JsonNode.Parse("""{"a": 42, "b": "text", "c": [1]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);
        DeltaApply.ApplyDelta(old, ops);

        Assert.True(DeltaDiff.DeepEquals(old, updated));
    }

    #endregion

    #region Deeply Nested Changes

    [Fact]
    public void Diff_DeeplyNested_5Levels_ProducesCorrectPath()
    {
        var old = JsonNode.Parse("""
        {
            "a": {
                "b": {
                    "c": {
                        "d": {
                            "e": 1
                        }
                    }
                }
            }
        }
        """)!;

        var updated = JsonNode.Parse("""
        {
            "a": {
                "b": {
                    "c": {
                        "d": {
                            "e": 2
                        }
                    }
                }
            }
        }
        """)!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(["a", "b", "c", "d", "e"], ops[0].Path);
        Assert.Equal(2, ops[0].Value?.GetValue<int>());
    }

    [Fact]
    public void RoundTrip_DeeplyNested_ProducesSameState()
    {
        var old = JsonNode.Parse("""
        {
            "level1": {
                "level2": {
                    "level3": {
                        "level4": {
                            "level5": {
                                "value": "original"
                            }
                        }
                    }
                }
            }
        }
        """)!;

        var updated = (JsonNode)old.DeepClone();
        updated["level1"]!["level2"]!["level3"]!["level4"]!["level5"]!["value"] = "changed";

        var ops = DeltaDiff.ComputeDelta(old, updated);
        DeltaApply.ApplyDelta(old, ops);

        Assert.True(DeltaDiff.DeepEquals(old, updated));
    }

    [Fact]
    public void Diff_DeeplyNested_AddNewProperty_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"a": {"b": {"c": {}}}}""")!;
        var updated = JsonNode.Parse("""{"a": {"b": {"c": {"d": "new"}}}}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal("new", ops[0].Value?.GetValue<string>());
    }

    [Fact]
    public void Diff_DeeplyNested_RemoveProperty_ProducesRemoveOp()
    {
        var old = JsonNode.Parse("""{"a": {"b": {"c": {"d": "old"}}}}""")!;
        var updated = JsonNode.Parse("""{"a": {"b": {"c": {}}}}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Remove, ops[0].Op);
    }

    #endregion

    #region Boolean and Numeric Edge Values

    [Fact]
    public void Diff_BooleanFlip_TrueToFalse_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"enabled": true}""")!;
        var updated = JsonNode.Parse("""{"enabled": false}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.False(ops[0].Value?.GetValue<bool>());
    }

    [Fact]
    public void Diff_BooleanFlip_FalseToTrue_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"enabled": false}""")!;
        var updated = JsonNode.Parse("""{"enabled": true}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.True(ops[0].Value?.GetValue<bool>());
    }

    [Fact]
    public void Diff_LargeNumbers_PreservesValue()
    {
        var old = JsonNode.Parse("""{"num": 0}""")!;
        var updated = JsonNode.Parse("""{"num": 9999999999999}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(9999999999999L, ops[0].Value?.GetValue<long>());
    }

    [Fact]
    public void Diff_FloatingPoint_PreservesValue()
    {
        var old = JsonNode.Parse("""{"val": 0.0}""")!;
        var updated = JsonNode.Parse("""{"val": 3.14159265358979}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
    }

    [Fact]
    public void Diff_NegativeNumber_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": 100}""")!;
        var updated = JsonNode.Parse("""{"val": -100}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(-100, ops[0].Value?.GetValue<int>());
    }

    [Fact]
    public void Diff_ZeroToNonZero_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"val": 0}""")!;
        var updated = JsonNode.Parse("""{"val": 1}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
    }

    #endregion

    #region Empty String vs Null

    [Fact]
    public void Diff_EmptyStringToNull_ProducesSetNullOp()
    {
        var old = JsonNode.Parse("""{"name": ""}""")!;
        var updated = JsonNode.Parse("""{"name": null}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.SetNull, ops[0].Op);
    }

    [Fact]
    public void Diff_NullToEmptyString_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"name": null}""")!;
        var updated = JsonNode.Parse("""{"name": ""}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal("", ops[0].Value?.GetValue<string>());
    }

    [Fact]
    public void Diff_EmptyStringToNonEmpty_ProducesSetOp()
    {
        var old = JsonNode.Parse("""{"name": ""}""")!;
        var updated = JsonNode.Parse("""{"name": "hello"}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
    }

    #endregion

    #region Array of Objects

    [Fact]
    public void Diff_ArrayOfObjects_ElementAdded_ProducesOp()
    {
        var old = JsonNode.Parse("""{"items": [{"id": 1}, {"id": 2}]}""")!;
        var updated = JsonNode.Parse("""{"items": [{"id": 1}, {"id": 2}, {"id": 3}]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.NotEmpty(ops);
    }

    [Fact]
    public void Diff_ArrayOfObjects_ElementRemoved_ProducesOp()
    {
        var old = JsonNode.Parse("""{"items": [{"id": 1}, {"id": 2}, {"id": 3}]}""")!;
        var updated = JsonNode.Parse("""{"items": [{"id": 1}, {"id": 3}]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.NotEmpty(ops);
    }

    [Fact]
    public void RoundTrip_ArrayOfObjects_ProducesSameState()
    {
        var old = JsonNode.Parse("""{"items": [{"id": 1, "name": "a"}, {"id": 2, "name": "b"}]}""")!;
        var updated = JsonNode.Parse("""{"items": [{"id": 1, "name": "a"}, {"id": 2, "name": "b"}, {"id": 3, "name": "c"}]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);
        DeltaApply.ApplyDelta(old, ops);

        Assert.True(DeltaDiff.DeepEquals(old, updated));
    }

    [Fact]
    public void Diff_EmptyArray_ToNonEmpty_ProducesOp()
    {
        var old = JsonNode.Parse("""{"items": []}""")!;
        var updated = JsonNode.Parse("""{"items": [1, 2, 3]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.NotEmpty(ops);
    }

    [Fact]
    public void Diff_NonEmptyArray_ToEmpty_ProducesOp()
    {
        var old = JsonNode.Parse("""{"items": [1, 2, 3]}""")!;
        var updated = JsonNode.Parse("""{"items": []}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        Assert.NotEmpty(ops);
    }

    [Fact]
    public void RoundTrip_ArrayEmpty_ToNonEmpty_ProducesSameState()
    {
        var old = JsonNode.Parse("""{"items": []}""")!;
        var updated = JsonNode.Parse("""{"items": ["a", "b", "c"]}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);
        DeltaApply.ApplyDelta(old, ops);

        Assert.True(DeltaDiff.DeepEquals(old, updated));
    }

    #endregion

    #region Multiple Simultaneous Changes

    [Fact]
    public void Diff_ManyFieldsChanged_AllCaptured()
    {
        var obj = new JsonObject();
        for (int i = 0; i < 50; i++)
        {
            obj[$"field_{i}"] = i + 1;
        }

        var updated = (JsonObject)obj.DeepClone();
        for (int i = 0; i < 50; i++)
        {
            updated[$"field_{i}"] = (i + 1) * 10;
        }

        var ops = DeltaDiff.ComputeDelta(obj, updated);

        Assert.Equal(50, ops.Count);
        Assert.All(ops, op => Assert.Equal(DeltaOp.Set, op.Op));
    }

    [Fact]
    public void RoundTrip_ManyFieldsChanged_ProducesSameState()
    {
        var obj = new JsonObject();
        for (int i = 0; i < 50; i++)
        {
            obj[$"field_{i}"] = i + 1;
        }

        var updated = (JsonObject)obj.DeepClone();
        for (int i = 0; i < 50; i++)
        {
            updated[$"field_{i}"] = (i + 1) * 10;
        }

        var ops = DeltaDiff.ComputeDelta(obj, updated);
        DeltaApply.ApplyDelta(obj, ops);

        Assert.True(DeltaDiff.DeepEquals(obj, updated));
    }

    [Fact]
    public void Diff_MixedOperations_AddRemoveModify_AllCaptured()
    {
        var old = JsonNode.Parse("""{"keep": 1, "modify": "old", "remove": true}""")!;
        var updated = JsonNode.Parse("""{"keep": 1, "modify": "new", "added": 42}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);

        // Should have: modify (Set), remove (Remove), added (Set)
        Assert.Equal(3, ops.Count);
    }

    [Fact]
    public void RoundTrip_MixedOperations_ProducesSameState()
    {
        var old = JsonNode.Parse("""{"keep": 1, "modify": "old", "remove": true}""")!;
        var updated = JsonNode.Parse("""{"keep": 1, "modify": "new", "added": 42}""")!;

        var ops = DeltaDiff.ComputeDelta(old, updated);
        DeltaApply.ApplyDelta(old, ops);

        Assert.True(DeltaDiff.DeepEquals(old, updated));
    }

    #endregion
}
