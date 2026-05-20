using NetConduit.Enums;
using NetConduit.Exceptions;

namespace NetConduit.UnitTests;

public sealed class PrefixSubscriptionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

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
    public async Task PrefixSubscription_RoutesMatchingChannels_AwayFromDefaultStream()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var prefixed = new List<string>();
        var defaultStream = new List<string>();

        using var prefixCts = new CancellationTokenSource(TestTimeout);
        using var defaultCts = new CancellationTokenSource(TestTimeout);

        // Registration is synchronous inside AcceptChannelsAsync; obtain the
        // enumerables on this thread to guarantee the prefix subscription is
        // live before any channel is opened.
        var prefixEnumerable = server.AcceptChannelsAsync("mesh/", prefixCts.Token);
        var defaultEnumerable = server.AcceptChannelsAsync(ct: defaultCts.Token);

        var prefixTask = Task.Run(async () =>
        {
            await foreach (var ch in prefixEnumerable)
                prefixed.Add(ch.ChannelId);
        });
        var defaultTask = Task.Run(async () =>
        {
            await foreach (var ch in defaultEnumerable)
                defaultStream.Add(ch.ChannelId);
        });

        client.OpenChannel("mesh/peer-a");
        client.OpenChannel("user/alice");
        client.OpenChannel("mesh/peer-b");
        client.OpenChannel("user/bob");

        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline && (prefixed.Count < 2 || defaultStream.Count < 2))
            await Task.Delay(20);

        Assert.Equal(2, prefixed.Count);
        Assert.Equal(2, defaultStream.Count);
        Assert.All(prefixed, id => Assert.StartsWith("mesh/", id));
        Assert.All(defaultStream, id => Assert.StartsWith("user/", id));

        prefixCts.Cancel();
        defaultCts.Cancel();
        await SwallowAsync(prefixTask);
        await SwallowAsync(defaultTask);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PrefixSubscription_DuplicatePrefix_ThrowsChannelExists()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var firstCts = new CancellationTokenSource(TestTimeout);
        var firstEnumerable = server.AcceptChannelsAsync("a/", firstCts.Token);
        var firstTask = Task.Run(async () =>
        {
            await foreach (var _ in firstEnumerable) { }
        });

        var ex = Assert.Throws<MultiplexerException>(() =>
            server.AcceptChannelsAsync("a/", CancellationToken.None));
        Assert.Equal(ErrorCode.ChannelExists, ex.ErrorCode);

        firstCts.Cancel();
        await SwallowAsync(firstTask);
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PrefixSubscription_OverlappingPrefix_ThrowsChannelExists()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var outerCts = new CancellationTokenSource(TestTimeout);
        var outerEnumerable = server.AcceptChannelsAsync("a", outerCts.Token);
        var outerTask = Task.Run(async () =>
        {
            await foreach (var _ in outerEnumerable) { }
        });

        // "ab" is a strict extension of "a" — any channel "ab*" would match both.
        var ex1 = Assert.Throws<MultiplexerException>(() =>
            server.AcceptChannelsAsync("ab", CancellationToken.None));
        Assert.Equal(ErrorCode.ChannelExists, ex1.ErrorCode);

        // Symmetric: candidate that is a prefix of an existing subscription is rejected too.
        var ex2 = Assert.Throws<MultiplexerException>(() =>
            server.AcceptChannelsAsync("", CancellationToken.None));
        Assert.Equal(ErrorCode.ChannelExists, ex2.ErrorCode);

        outerCts.Cancel();
        await SwallowAsync(outerTask);
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PrefixSubscription_CancellationFreesPrefix_AllowsResubscribe()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Register the first subscription synchronously and drive it via an
        // enumerator so cancellation cleanup actually runs.
        using var firstCts = new CancellationTokenSource();
        var firstEnumerator = server.AcceptChannelsAsync("ns/", firstCts.Token).GetAsyncEnumerator();

        firstCts.Cancel();
        try { while (await firstEnumerator.MoveNextAsync()) { } }
        catch (OperationCanceledException) { }
        await firstEnumerator.DisposeAsync();

        // Register a fresh subscription synchronously, then open the channel.
        using var secondCts = new CancellationTokenSource(TestTimeout);
        var secondEnumerator = server.AcceptChannelsAsync("ns/", secondCts.Token).GetAsyncEnumerator();
        try
        {
            client.OpenChannel("ns/foo");
            var moved = await secondEnumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout);
            Assert.True(moved);
            Assert.Equal("ns/foo", secondEnumerator.Current.ChannelId);
        }
        finally
        {
            secondCts.Cancel();
            try { await secondEnumerator.DisposeAsync(); } catch (OperationCanceledException) { }
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PrefixSubscription_CancellationReRoutesBufferedChannels_ToDefaultStream()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Register the subscription but never call MoveNextAsync — channels
        // routed to it stay buffered in its internal queue.
        using var subCts = new CancellationTokenSource();
        var subEnumerator = server.AcceptChannelsAsync("tmp/", subCts.Token).GetAsyncEnumerator();

        client.OpenChannel("tmp/x");
        client.OpenChannel("tmp/y");
        // Give the server's reader loop time to dispatch both INIT frames into
        // the subscription queue.
        await Task.Delay(250);

        // Cancel + dispose. The `finally` in the iterator drains buffered items
        // back into the default accept stream.
        subCts.Cancel();
        try { while (await subEnumerator.MoveNextAsync()) { } }
        catch (OperationCanceledException) { }
        await subEnumerator.DisposeAsync();

        using var defaultCts = new CancellationTokenSource(TestTimeout);
        var defaultEnumerator = server.AcceptChannelsAsync(ct: defaultCts.Token).GetAsyncEnumerator();
        try
        {
            var observed = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var moved = await defaultEnumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout);
                Assert.True(moved);
                observed.Add(defaultEnumerator.Current.ChannelId);
            }
            Assert.Contains("tmp/x", observed);
            Assert.Contains("tmp/y", observed);
        }
        finally
        {
            defaultCts.Cancel();
            try { await defaultEnumerator.DisposeAsync(); } catch (OperationCanceledException) { }
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t; }
        catch (OperationCanceledException) { }
        catch (MultiplexerException) { }
    }

    [Fact]
    public async Task AcceptChannel_TakesPriorityOverMatchingPrefixSubscription()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var prefixCts = new CancellationTokenSource(TestTimeout);
        var prefixed = new List<string>();
        var prefixEnumerable = server.AcceptChannelsAsync("x/", prefixCts.Token);

        // Register the exact-id accept before the channel is opened.
        var exactWaiter = server.AcceptChannel("x/specific");

        var prefixTask = Task.Run(async () =>
        {
            await foreach (var ch in prefixEnumerable)
                prefixed.Add(ch.ChannelId);
        });

        // Open both a channel that should match only the prefix, and the
        // channel claimed by the exact-id waiter.
        client.OpenChannel("x/other");
        client.OpenChannel("x/specific");

        // Wait for the prefix subscription to observe the non-claimed channel.
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline && prefixed.Count < 1)
            await Task.Delay(20);

        Assert.Equal("x/specific", exactWaiter.ChannelId);
        Assert.Single(prefixed);
        Assert.Equal("x/other", prefixed[0]);
        Assert.DoesNotContain("x/specific", prefixed);

        prefixCts.Cancel();
        await SwallowAsync(prefixTask);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
