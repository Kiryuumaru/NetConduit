using System.Text.Json.Serialization;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

[JsonSerializable(typeof(LeakState))]
internal partial class LeakStateContext : JsonSerializerContext { }

public sealed record LeakState(int N);

public sealed class DeltaMessageTransitAsyncExtensionLeakTests
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

    [Fact]
    public async Task OpenDeltaMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.OpenDeltaMessageTransitAsync("delta", LeakStateContext.Default.LeakState, cancellationToken: cts.Token);
        });

        await using var transit = client.OpenDeltaMessageTransit("delta", LeakStateContext.Default.LeakState);
    }

    [Fact]
    public async Task AcceptDeltaMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.AcceptDeltaMessageTransitAsync("delta-accept", LeakStateContext.Default.LeakState, cancellationToken: cts.Token);
        });

        await using var transit = client.AcceptDeltaMessageTransit("delta-accept", LeakStateContext.Default.LeakState);
    }

    [Fact]
    public async Task AcceptReceiveOnlyDeltaMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.AcceptReceiveOnlyDeltaMessageTransitAsync("delta-recv", LeakStateContext.Default.LeakState, cancellationToken: cts.Token);
        });

        await using var transit = client.AcceptReceiveOnlyDeltaMessageTransit("delta-recv", LeakStateContext.Default.LeakState);
    }
}
