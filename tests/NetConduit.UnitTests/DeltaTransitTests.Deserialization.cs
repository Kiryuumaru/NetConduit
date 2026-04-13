using System.Text;
using System.Text.Json;
using NetConduit.Enums;
using NetConduit.Internal;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

public partial class DeltaTransitTests
{
    #region DeserializeDelta Validation

    [Fact]
    public void DeserializeDelta_MalformedInput_Throws()
    {
        var malformedJson = Encoding.UTF8.GetBytes("not json at all");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(malformedJson));
    }

    [Fact]
    public void DeserializeDelta_EmptyBytes_Throws()
    {
        Assert.ThrowsAny<Exception>(() => DeltaTransit<SimpleState>.DeserializeDelta(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DeserializeDelta_InvalidJson_Throws()
    {
        var garbage = Encoding.UTF8.GetBytes("{not valid}");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(garbage));
    }

    [Fact]
    public void DeserializeDelta_NonArrayRoot_Throws()
    {
        var objectInsteadOfArray = Encoding.UTF8.GetBytes("""{"op": "set"}""");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(objectInsteadOfArray));
    }

    [Fact]
    public void DeserializeDelta_TruncatedArray_Throws()
    {
        var truncated = Encoding.UTF8.GetBytes("""[[0], [0, ["name"]]]""");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(truncated));
    }

    [Fact]
    public void DeserializeDelta_EmptyArray_ReturnsEmpty()
    {
        var emptyArray = Encoding.UTF8.GetBytes("[]");
        var ops = DeltaTransit<SimpleState>.DeserializeDelta(emptyArray);
        Assert.Empty(ops);
    }

    [Fact]
    public void DeserializeDelta_InvalidOpCode_Throws()
    {
        var invalidOp = Encoding.UTF8.GetBytes("""[[99, ["field"]]]""");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(invalidOp));
    }

    [Fact]
    public void DeserializeDelta_NegativeArrayIndex_Parsed()
    {
        var negativeIndex = Encoding.UTF8.GetBytes("""[[12, ["arr"], -1]]""");
        var ops = DeltaTransit<SimpleState>.DeserializeDelta(negativeIndex);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.ArrayRemove, ops[0].Op);
        Assert.Equal(-1, ops[0].Index);
    }

    [Fact]
    public void DeserializeDelta_MissingValueForSetOp_NullValue()
    {
        var noValue = Encoding.UTF8.GetBytes("""[[0, ["field"]]]""");
        var ops = DeltaTransit<SimpleState>.DeserializeDelta(noValue);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Null(ops[0].Value);
    }

    [Fact]
    public void DeserializeDelta_StringInsteadOfArray_Throws()
    {
        var stringJson = Encoding.UTF8.GetBytes("\"hello\"");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(stringJson));
    }

    [Fact]
    public void DeserializeDelta_NumberRoot_Throws()
    {
        var numberJson = Encoding.UTF8.GetBytes("42");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(numberJson));
    }

    [Fact]
    public void DeserializeDelta_NullRoot_Throws()
    {
        var nullJson = Encoding.UTF8.GetBytes("null");
        Assert.ThrowsAny<JsonException>(() => DeltaTransit<SimpleState>.DeserializeDelta(nullJson));
    }

    [Fact]
    public void DeserializeDelta_ValidSetOp_ParsedCorrectly()
    {
        var validDelta = Encoding.UTF8.GetBytes("""[[0, ["name"], "John"]]""");
        var ops = DeltaTransit<SimpleState>.DeserializeDelta(validDelta);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal("John", ops[0].Value!.GetValue<string>());
    }

    #endregion
}
