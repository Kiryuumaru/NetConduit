using NetConduit.Exceptions;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class ChannelRegistryTests
{
    /// <summary>
    /// Regression for #268. When RegisterWriteChannel succeeds on the per-index
    /// insert but loses the per-id race (a channel with the same ChannelId was
    /// already registered at a different index), the throw must NOT leave the
    /// per-index entry populated — that would orphan the channel in _writeChannels,
    /// permanently consume its index, and make GetWriteChannel(index) return a
    /// channel the caller has no reference to.
    /// </summary>
    [Fact]
    public void RegisterWriteChannel_IdCollision_RollsBackIndexEntry()
    {
        var registry = new ChannelRegistry(useOddIndices: true);
        var owner = new NoopOwner();

        var first = new WriteChannel("dup", 1, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(30), owner);
        registry.RegisterWriteChannel(1, first);

        // Second registration at a different index but with the same ChannelId
        // must throw and must NOT leave the orphan in _writeChannels[3].
        var second = new WriteChannel("dup", 3, ChannelPriority.Normal, 64 * 1024, TimeSpan.FromSeconds(30), owner);
        Assert.Throws<MultiplexerException>(() => registry.RegisterWriteChannel(3, second));

        Assert.Same(first, registry.GetWriteChannel(1));
        Assert.Null(registry.GetWriteChannel(3));
    }

    /// <summary>
    /// Regression for #268 (ReadChannel side). Same semantics as the write-side
    /// test above: per-index entry must be rolled back when the per-id insert
    /// loses the race.
    /// </summary>
    [Fact]
    public void RegisterReadChannel_IdCollision_RollsBackIndexEntry()
    {
        var registry = new ChannelRegistry(useOddIndices: false);

        var first = new ReadChannel("dup", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, first);

        var second = new ReadChannel("dup", 4, ChannelPriority.Normal, 64 * 1024);
        Assert.Throws<MultiplexerException>(() => registry.RegisterReadChannel(4, second));

        Assert.Same(first, registry.GetReadChannel(2));
        Assert.Null(registry.GetReadChannel(4));
    }

    /// <summary>
    /// Regression for #179 / #229. The fast-path scenario: an inbound INIT
    /// has registered the channel in _idToIndex BEFORE AcceptChannelAsync is
    /// called, but EnqueueForAccept has not yet fired. AcceptChannelAsync
    /// must register its TCS first, complete it via fast-path with the
    /// existing channel, and EnqueueForAccept must then observe the TCS
    /// (completed with this same channel) and SUPPRESS the _acceptQueue
    /// fall-through. Otherwise the same ReadChannel is delivered both to the
    /// named waiter AND to a generic AcceptChannelsAsync(null) consumer.
    /// </summary>
    [Fact]
    public async Task AcceptChannelAsync_FastPathThenEnqueue_DeliversOnceNoQueueFallthrough()
    {
        var registry = new ChannelRegistry(useOddIndices: false);
        var channel = new ReadChannel("foo", 2, ChannelPriority.Normal, 64 * 1024);

        // Simulate the dispatcher's RegisterReadChannel having just committed
        // (so _idToIndex hits) but EnqueueForAccept not yet called.
        registry.RegisterReadChannel(2, channel);

        // AcceptChannelAsync's fast path completes the TCS with `channel`.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = await registry.AcceptChannelAsync("foo", cts.Token);
        Assert.Same(channel, accepted);

        // Now the dispatcher fires EnqueueForAccept(channel). With the fix,
        // this must NOT also write `channel` into _acceptQueue.
        registry.EnqueueForAccept(channel);

        // Drain the generic accept stream with a short deadline; nothing
        // should appear.
        using var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stream = registry.AcceptChannelsAsync(channelIdPrefix: null, drainCts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(drainCts.Token))
                Assert.Fail("EnqueueForAccept double-delivered the channel into _acceptQueue after AcceptChannelAsync's fast path.");
        });
    }

    /// <summary>
    /// Regression for #179 / #229 — the canonical race: AcceptChannelAsync
    /// registers its TCS, then the dispatcher commits RegisterReadChannel
    /// (so a re-check would hit _idToIndex). The TCS-routing fix completes
    /// the TCS via fast-path; the dispatcher's subsequent EnqueueForAccept
    /// must suppress its _acceptQueue write since the named waiter already
    /// owns the channel.
    /// </summary>
    [Fact]
    public async Task AcceptChannelAsync_RaceWindowBetweenRegisterAndEnqueue_DoesNotDoubleDeliver()
    {
        var registry = new ChannelRegistry(useOddIndices: false);
        var channel = new ReadChannel("foo", 2, ChannelPriority.Normal, 64 * 1024);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Start the named accept first. The TCS is in _pendingAccepts; the
        // task has not yet completed because _idToIndex does not yet contain
        // "foo".
        var acceptTask = registry.AcceptChannelAsync("foo", cts.Token).AsTask();
        Assert.False(acceptTask.IsCompleted);

        // Dispatcher commits the registry side, but EnqueueForAccept has not
        // yet been called. With the fix, this RegisterReadChannel does not
        // by itself complete the TCS — only EnqueueForAccept does. So the
        // task is still pending.
        registry.RegisterReadChannel(2, channel);
        Assert.False(acceptTask.IsCompleted);

        // Now the dispatcher fires EnqueueForAccept. The TCS in
        // _pendingAccepts wins (still uncompleted); the channel is delivered
        // to the named waiter only, never to _acceptQueue.
        registry.EnqueueForAccept(channel);

        var accepted = await acceptTask;
        Assert.Same(channel, accepted);

        // Generic accept stream must remain empty.
        using var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stream = registry.AcceptChannelsAsync(channelIdPrefix: null, drainCts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(drainCts.Token))
                Assert.Fail("Channel was double-delivered into _acceptQueue.");
        });
    }

    /// <summary>
    /// Regression for #179 / #229 — close+reopen edge case. After a successful
    /// AcceptChannelAsync via fast-path, the TCS is left completed in
    /// _pendingAccepts. If the same channel id is later closed and re-INITed,
    /// EnqueueForAccept must NOT silently drop the new channel (which would
    /// happen if the stale TCS's TrySetResult-fail short-circuited the
    /// fall-through). The ReferenceEquals guard lets the new channel through
    /// to the generic queue.
    /// </summary>
    [Fact]
    public async Task EnqueueForAccept_StaleCompletedTcsForReopenedChannelId_FallsThroughToQueue()
    {
        var registry = new ChannelRegistry(useOddIndices: false);

        // First channel: arrives, AcceptChannelAsync fast-paths it.
        var first = new ReadChannel("foo", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, first);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = await registry.AcceptChannelAsync("foo", cts.Token);
        Assert.Same(first, accepted);

        // Close + unregister + re-INIT with a new channel under the same id.
        registry.UnregisterChannel(2, "foo");
        var second = new ReadChannel("foo", 4, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(4, second);

        // No new AcceptChannelAsync this time. EnqueueForAccept must fall
        // through to _acceptQueue because the stale TCS still in
        // _pendingAccepts holds `first`, not `second`.
        registry.EnqueueForAccept(second);

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var ch in registry.AcceptChannelsAsync(channelIdPrefix: null, drainCts.Token).WithCancellation(drainCts.Token))
        {
            Assert.Same(second, ch);
            return; // got it
        }
        Assert.Fail("Reopened channel was silently dropped by EnqueueForAccept.");
    }

    private sealed class NoopOwner : IChannelOwner
    {
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public void SendAck(ushort channelIndex, ulong consumedPosition) { }
        public void NotifyEventHandlerException(Exception exception) { }
    }
}
