namespace NetConduit.UnitTests;

/// <summary>
/// Mid-wait cancellation coverage for OpenChannelAsync / AcceptChannelAsync:
/// the token fires while WaitForReadyAsync is parked (not pre-cancelled),
/// and the channel slot must still be released for reuse.
/// </summary>
[Collection("Sequential")]
public sealed class ChannelAsyncMidWaitCancelTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
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
        return (client, server);
    }

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> StartedPairAsync()
    {
        var (c, s) = CreatePair();
        c.Start(); s.Start();
        await Task.WhenAll(c.WaitForReadyAsync(), s.WaitForReadyAsync());
        return (c, s);
    }

    [Fact]
    public async Task OpenChannelAsync_CancelledMidWait_ReleasesChannelIdSlot()
    {
        // Park the wait deterministically: start only the client so no peer
        // ever ACKs the INIT. The async open stays in-flight until the
        // cancellation fires mid-wait (not pre-cancelled).
        var (client, server) = CreatePair();
        client.Start();

        const string channelId = "mid-wait-cancel";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var openTask = client.OpenChannelAsync(channelId, cts.Token);
        await Task.Delay(100);
        Assert.False(openTask.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await openTask);

        var channel = client.OpenChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_RepeatedMidWaitCancel_DoesNotPoisonChannelIdSlot()
    {
        var (client, server) = CreatePair();
        client.Start();

        const int iterations = 20;
        const string channelId = "rapid-mid-wait-cancel";

        for (int i = 0; i < iterations; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var openTask = client.OpenChannelAsync(channelId, cts.Token);
            await Task.Delay(10);
            if (!openTask.IsCompleted)
                await cts.CancelAsync();
            try
            {
                await openTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.ChannelExists)
            {
                Assert.Fail(
                    $"ChannelId '{channelId}' was poisoned after iteration {i} — " +
                    "OpenChannelAsync did not release the channel on mid-wait cancellation.");
            }
        }

        var channel = client.OpenChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelAsync_CancelledMidWait_ReleasesChannelIdSlot()
    {
        var (client, server) = await StartedPairAsync();
        await using var _c = client;
        await using var _s = server;

        const string channelId = "mid-wait-accept";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptTask = server.AcceptChannelAsync(channelId, cts.Token).AsTask();
        await Task.Delay(100);
        Assert.False(acceptTask.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await acceptTask);

        var channel = server.AcceptChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();
    }
}
