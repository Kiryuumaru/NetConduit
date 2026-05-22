using NetConduit.Enums;
using NetConduit.Exceptions;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for issue #250: OpenChannelAsync / AcceptChannelAsync must
/// dispose the channel when WaitForReadyAsync throws, otherwise the channelId,
/// slab, and wire-index are leaked permanently.
/// </summary>
[Collection("Sequential")]
public sealed class ChannelAsyncHelperLeakTests
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

    [Fact]
    public async Task OpenChannelAsync_PreCancelledToken_ReleasesChannelIdSlot()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const string channelId = "leak-test";

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await client.OpenChannelAsync(channelId, cts.Token));
        }

        // After the cancelled OpenChannelAsync, the channelId slot in the local
        // registry must be reusable. Without the fix, OpenChannel throws
        // MultiplexerException(ChannelExists) because the leaked channel still
        // occupies the slot.
        var channel = client.OpenChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_WithOptions_PreCancelledToken_ReleasesChannelIdSlot()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const string channelId = "leak-test-opts";
        var opts = new ChannelOptions { ChannelId = channelId };

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await client.OpenChannelAsync(opts, cts.Token));
        }

        var channel = client.OpenChannel(new ChannelOptions { ChannelId = channelId });
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelAsync_PreCancelledToken_ReleasesChannelIdSlot()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const string channelId = "leak-test-accept";

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await server.AcceptChannelAsync(channelId, cts.Token));
        }

        // After cancellation the channelId slot must be reusable for a new accept.
        var channel = server.AcceptChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_RepeatedCancellation_DoesNotPoisonChannelIdSlot()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        const int iterations = 100;
        const string channelId = "rapid-cancel";

        for (int i = 0; i < iterations; i++)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await client.OpenChannelAsync(channelId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.ChannelExists)
            {
                Assert.Fail(
                    $"ChannelId '{channelId}' was poisoned after iteration {i} — " +
                    "OpenChannelAsync did not release the channel on cancellation.");
            }
        }

        // Final reuse must still succeed.
        var channel = client.OpenChannel(channelId);
        Assert.NotNull(channel);
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
