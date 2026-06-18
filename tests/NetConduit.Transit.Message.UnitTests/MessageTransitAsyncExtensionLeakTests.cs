using System.Text.Json.Serialization;

namespace NetConduit.Transit.Message.UnitTests;

[JsonSerializable(typeof(LeakProbe))]
internal partial class LeakProbeContext : JsonSerializerContext { }

public sealed record LeakProbe(int N);

public sealed class MessageTransitAsyncExtensionLeakTests
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
    public async Task OpenMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.OpenMessageTransitAsync("probe", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe, cancellationToken: cts.Token);
        });

        // Channels must be released so the same channelId can be opened again without ChannelExistsException.
        await using var transit = client.OpenMessageTransit("probe", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe);
    }

    [Fact]
    public async Task AcceptMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.AcceptMessageTransitAsync("probe-accept", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe, cancellationToken: cts.Token);
        });

        await using var transit = client.AcceptMessageTransit("probe-accept", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe);
    }

    [Fact]
    public async Task AcceptReceiveOnlyMessageTransitAsync_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.AcceptReceiveOnlyMessageTransitAsync("probe-recv", LeakProbeContext.Default.LeakProbe, cancellationToken: cts.Token);
        });

        await using var transit = client.AcceptReceiveOnlyMessageTransit("probe-recv", LeakProbeContext.Default.LeakProbe);
    }

    [Fact]
    public async Task OpenMessageTransitAsync_DistinctChannelIds_CancelledBeforeReady_ReleasesChannels()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.OpenMessageTransitAsync("w", "r", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe, cancellationToken: cts.Token);
        });

        await using var transit = client.OpenMessageTransit("w", "r", LeakProbeContext.Default.LeakProbe, LeakProbeContext.Default.LeakProbe);
    }
}
