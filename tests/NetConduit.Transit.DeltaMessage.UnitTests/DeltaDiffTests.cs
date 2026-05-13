using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

public sealed class DeltaDiffTests
{
    [Fact]
    public void IdenticalObjects_EmptyDelta()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice","age":30}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Empty(ops);
    }

    [Fact]
    public void PropertyChange_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice","age":31}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(new object[] { "age" }, ops[0].Path);
    }

    [Fact]
    public void PropertyAdded_ProducesSetOp()
    {
        var a = JsonNode.Parse("""{"name":"alice"}""");
        var b = JsonNode.Parse("""{"name":"alice","email":"alice@test.com"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Contains("email", ops[0].Path.Cast<string>());
    }

    [Fact]
    public void PropertyRemoved_ProducesRemoveOp()
    {
        var a = JsonNode.Parse("""{"name":"alice","age":30}""");
        var b = JsonNode.Parse("""{"name":"alice"}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Remove, ops[0].Op);
        Assert.Equal(new object[] { "age" }, ops[0].Path);
    }

    [Fact]
    public void NestedChange_ProducesNestedPath()
    {
        var a = JsonNode.Parse("""{"user":{"name":"alice","age":30}}""");
        var b = JsonNode.Parse("""{"user":{"name":"alice","age":31}}""");

        var ops = DeltaDiff.ComputeDelta(a!, b!);
        Assert.Single(ops);
        Assert.Equal(DeltaOp.Set, ops[0].Op);
        Assert.Equal(2, ops[0].Path.Length);
    }

    [Fact]
    public void SetOp_ModifiesValue()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["age"], JsonValue.Create(31), null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        Assert.Equal(31, node!["age"]!.GetValue<int>());
    }

    [Fact]
    public void RemoveOp_RemovesProperty()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Remove, ["age"], null, null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        Assert.Null(node!["age"]);
    }

    [Fact]
    public void SetNull_SetsNull()
    {
        var node = JsonNode.Parse("""{"name":"alice","age":30}""");
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.SetNull, ["age"], null, null)
        };

        DeltaApply.ApplyDelta(node!, ops);
        var obj = node!.AsObject();
        Assert.True(obj.ContainsKey("age"));
    }

    [Fact]
    public void ComputeAndApply_Roundtrip()
    {
        var original = JsonNode.Parse("""{"players":[{"id":1,"name":"alice","score":100},{"id":2,"name":"bob","score":200}],"status":"playing"}""");
        var modified = JsonNode.Parse("""{"players":[{"id":1,"name":"alice","score":150},{"id":2,"name":"bob","score":200}],"status":"paused"}""");

        var ops = DeltaDiff.ComputeDelta(original!, modified!);
        Assert.NotEmpty(ops);

        DeltaApply.ApplyDelta(original!, ops);

        Assert.Equal("paused", original!["status"]!.GetValue<string>());
        var players = original["players"]!.AsArray();
        Assert.Equal(150, players[0]!["score"]!.GetValue<int>());
    }

    [Fact]
    public void SerializeDelta_Roundtrips()
    {
        var ops = new List<DeltaOperation>
        {
            new(DeltaOp.Set, ["name"], JsonValue.Create("bob"), null),
            new(DeltaOp.Remove, ["age"], null, null),
            new(DeltaOp.ArrayInsert, ["items"], JsonValue.Create("new-item"), 2),
        };

        var json = DeltaMessageTransit<JsonObject>.SerializeDelta(ops);
        var deserialized = DeltaMessageTransit<JsonObject>.DeserializeDelta(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(3, deserialized.Count);

        Assert.Equal(DeltaOp.Set, deserialized[0].Op);
        Assert.Equal(new object[] { "name" }, deserialized[0].Path);

        Assert.Equal(DeltaOp.Remove, deserialized[1].Op);
        Assert.Equal(new object[] { "age" }, deserialized[1].Path);

        Assert.Equal(DeltaOp.ArrayInsert, deserialized[2].Op);
        Assert.Equal(2, deserialized[2].Index);
    }
}
