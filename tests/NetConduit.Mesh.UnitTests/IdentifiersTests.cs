namespace NetConduit.Mesh.UnitTests;

public class IdentifiersTests
{
    [Theory]
    [InlineData("node-1")]
    [InlineData("a")]
    [InlineData("alpha.beta_gamma~delta")]
    public void ValidateNodeId_AcceptsValidIds(string value)
    {
        var ex = Record.Exception(() => Identifiers.ValidateNodeId(value, "NodeId"));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateNodeId_AcceptsMaxLength()
    {
        string value = new string('x', Identifiers.MaxLength);
        var ex = Record.Exception(() => Identifiers.ValidateNodeId(value, "NodeId"));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateNodeId_RejectsTooLong()
    {
        string value = new string('x', Identifiers.MaxLength + 1);
        Assert.Throws<ArgumentException>(() => Identifiers.ValidateNodeId(value, "NodeId"));
    }

    [Fact]
    public void ValidateNodeId_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Identifiers.ValidateNodeId(null!, "NodeId"));
    }

    [Fact]
    public void ValidateNodeId_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => Identifiers.ValidateNodeId("", "NodeId"));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    public void ValidateNodeId_RejectsReservedChars(string value)
    {
        Assert.Throws<ArgumentException>(() => Identifiers.ValidateNodeId(value, "NodeId"));
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    public void ValidateNodeId_RejectsControlChars(string value)
    {
        Assert.Throws<ArgumentException>(() => Identifiers.ValidateNodeId(value, "NodeId"));
    }
}
