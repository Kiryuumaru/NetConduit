namespace NetConduit.Mesh.UnitTests;

public class DeterministicSessionIdTests
{
    [Fact]
    public void Compute_IsSymmetric()
    {
        var a = DeterministicSessionId.Compute("node-A", "node-B", "session-1");
        var b = DeterministicSessionId.Compute("node-B", "node-A", "session-1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DiffersForDifferentMultiplexerIds()
    {
        var a = DeterministicSessionId.Compute("node-A", "node-B", "session-1");
        var b = DeterministicSessionId.Compute("node-A", "node-B", "session-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DiffersForDifferentNodes()
    {
        var a = DeterministicSessionId.Compute("node-A", "node-B", "session-1");
        var b = DeterministicSessionId.Compute("node-A", "node-C", "session-1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_RejectsNullNodes()
    {
        Assert.Throws<ArgumentNullException>(() => DeterministicSessionId.Compute(null!, "b", "m"));
        Assert.Throws<ArgumentNullException>(() => DeterministicSessionId.Compute("a", null!, "m"));
        Assert.Throws<ArgumentNullException>(() => DeterministicSessionId.Compute("a", "b", null!));
    }

    [Fact]
    public void Compute_StableAcrossCalls()
    {
        var a = DeterministicSessionId.Compute("alpha", "beta", "gamma");
        var b = DeterministicSessionId.Compute("alpha", "beta", "gamma");
        Assert.Equal(a, b);
    }
}
