namespace NetConduit.Mesh.IntegrationTests;

/// <summary>
/// Helpers to spin up StreamMultiplexer pairs over an in-memory DuplexMemoryStream.
/// </summary>
internal static class MuxFixture
{
    internal static async Task<(StreamMultiplexer A, StreamMultiplexer B, DuplexMemoryStream Backing)> CreateMuxPairAsync(CancellationToken ct)
    {
        var duplex = new DuplexMemoryStream();
        var a = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var b = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        a.Start();
        b.Start();
        await Task.WhenAll(a.WaitForReadyAsync(ct), b.WaitForReadyAsync(ct));
        return (a, b, duplex);
    }
}
