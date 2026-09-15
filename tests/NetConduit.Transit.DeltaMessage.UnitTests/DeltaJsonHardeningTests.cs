using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

// Issue #618: peer-supplied JSON that fits the 16MB frame but is hostile in
// shape (deep nesting past 64, token count past 1M) must be rejected with
// JsonException at parse time on both the full-state and delta paths.
public sealed class DeltaJsonHardeningTests
{
    [Fact]
    public void DeserializeDelta_DepthOver64_ThrowsJsonException()
    {
        var deep = new string('[', 70) + new string(']', 70);
        // Wrap as a delta payload: depth violation triggers during parse.
        var payload = Encoding.UTF8.GetBytes(deep);
        // Depth violations surface as JsonReaderException, which derives
        // from JsonException: ThrowsAny accepts the contract, not the leaf.
        Assert.ThrowsAny<JsonException>(() =>
            DeltaMessageTransit<JsonObject>.DeserializeDelta(payload));
    }

    [Fact]
    public void ParseNode_DepthOver64_ThrowsJsonException()
    {
        var deep = Encoding.UTF8.GetBytes("{\"a\":" + new string('[', 70) + "1" + new string(']', 70) + "}");
        Assert.ThrowsAny<JsonException>(() => JsonHardening.ParseNode(deep));
    }

    [Fact]
    public void ParseNode_TokenBudgetExceeded_ThrowsJsonException()
    {
        // 1M+ scalar tokens in one array: fits any byte cap, must be gated.
        var sb = new StringBuilder("[");
        for (int i = 0; i < 1_000_005; i++)
            sb.Append(i).Append(',');
        sb.Append("0]");
        var payload = Encoding.UTF8.GetBytes(sb.ToString());
        var ex = Assert.Throws<JsonException>(() => JsonHardening.ParseNode(payload));
        Assert.Contains("token budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseNode_Boundary64_RoundTrips()
    {
        // 63 nested arrays + scalar: depth 64 exactly, must pass.
        var payload = Encoding.UTF8.GetBytes(new string('[', 63) + "1" + new string(']', 63));
        var node = JsonHardening.ParseNode(payload);
        Assert.NotNull(node);
    }

    [Fact]
    public void ClampSerializerOptions_NeverMutatesCaller_WideClampedDown()
    {
        var caller = new JsonSerializerOptions { MaxDepth = 128 };
        var effective = TransitJsonLimits.ClampSerializerOptions(caller);
        Assert.Equal(64, effective.MaxDepth);
        Assert.Equal(128, caller.MaxDepth);
    }

    [Fact]
    public void DocumentOptions_Over64_ThrowsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransitJsonLimits.DocumentOptions(65));
    }
}
