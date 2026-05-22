using Xunit;

namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitEventDedupTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    // Regression for #191. Pre-fix the transit forwarded each underlying
    // half's Disconnected event independently, raising Disconnected twice per
    // logical disconnect. Post-fix the transit latches a single fire.
    [Fact]
    public async Task Disconnected_FiresExactlyOnce_WhenBothHalvesDisconnect()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            var write = client.OpenChannel("disc-dedup>>");
            var read = await server.AcceptChannelAsync("disc-dedup>>", CancellationToken.None);

            var transit = new DuplexStreamTransit(write, read);
            int count = 0;
            transit.Disconnected += (_, _) => Interlocked.Increment(ref count);

            await transit.WaitForReadyAsync();

            await client.DisposeAsync();
            await server.DisposeAsync();

            await Task.Delay(200);

            Assert.Equal(1, count);

            await transit.DisposeAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
