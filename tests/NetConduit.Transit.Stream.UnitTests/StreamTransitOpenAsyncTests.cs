using Xunit;

namespace NetConduit.Transit.Stream.UnitTests;

public sealed class StreamTransitOpenAsyncTests
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
    public async Task OpenStreamAsync_String_WaitsForReady_TransfersBytes()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        server.AcceptStream("open-async-str");
        await using var write = await client.OpenStreamAsync("open-async-str");
        await using var read = await server.AcceptStreamAsync("open-async-str");

        Assert.True(write.IsReady);
        Assert.True(write.CanWrite);
        Assert.False(write.CanRead);
        Assert.Equal("open-async-str", write.WriteChannelId);

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await write.WriteAsync(payload);

        var buf = new byte[payload.Length];
        var n = await read.ReadAsync(buf);
        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, buf);
    }

    [Fact]
    public async Task OpenStreamAsync_Options_PreservesOptions_TransfersBytes()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        server.AcceptStream("open-async-opts");
        await using var write = await client.OpenStreamAsync(new ChannelOptions
        {
            ChannelId = "open-async-opts",
            Priority = ChannelPriority.High,
        });
        await using var read = await server.AcceptStreamAsync("open-async-opts");

        Assert.True(write.IsReady);
        Assert.True(write.CanWrite);
        Assert.Equal("open-async-opts", write.WriteChannelId);

        var payload = new byte[] { 9, 8, 7 };
        await write.WriteAsync(payload);

        var buf = new byte[payload.Length];
        var n = await read.ReadAsync(buf);
        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, buf);
    }

    [Fact]
    public async Task OpenStreamAsync_String_CancelledBeforeReady_ReleasesChannel()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.OpenStreamAsync("probe-open", cts.Token);
        });

        // Channel must be released so the same channelId can be opened again without ChannelExistsException.
        await using var transit = client.OpenStream("probe-open");
    }

    [Fact]
    public async Task OpenStreamAsync_Options_CancelledBeforeReady_ReleasesChannel()
    {
        var (client, server) = await CreateReadyPairAsync();
        await using var _c = client;
        await using var _s = server;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.OpenStreamAsync(new ChannelOptions { ChannelId = "probe-open-opts" }, cts.Token);
        });

        // Channel must be released so the same channelId can be opened again without ChannelExistsException.
        await using var transit = client.OpenStream(new ChannelOptions { ChannelId = "probe-open-opts" });
    }
}
