namespace NetConduit.Mesh.IntegrationTests;

internal static class MeshTestAssertions
{
    internal static async Task AssertRelayBytesForwardedAtLeastAsync(
        MeshMultiplexer mesh,
        long expected,
        CancellationToken ct)
    {
        for (int i = 0; i < 100 && mesh.Stats.RelayBytesForwarded < expected; i++)
        {
            await Task.Delay(20, ct);
        }

        long actual = mesh.Stats.RelayBytesForwarded;
        Assert.True(actual >= expected, $"Expected >= {expected}, got {actual}");
    }
}
