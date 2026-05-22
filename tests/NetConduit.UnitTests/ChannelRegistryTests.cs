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

    /// <summary>
    /// Regression for #228. UnregisterChannel must scope its _idToIndex removal
    /// to the (channelId, index) pair: a call with a channelId that resolves to
    /// a *different* index must NOT remove the legitimate mapping. Otherwise a
    /// well-meaning cleanup of a failed registration (or any future caller that
    /// passes a mismatched pair) silently breaks GetReadChannelById /
    /// GetWriteChannelById for the channel that legitimately owns the ID.
    /// </summary>
    [Fact]
    public void UnregisterChannel_MismatchedIndex_DoesNotRemoveLegitimateIdMapping()
    {
        var registry = new ChannelRegistry(useOddIndices: false);

        var legitimate = new ReadChannel("shared-id", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, legitimate);

        // Mismatched call: index 4 was never registered for "shared-id". This can
        // arise from a caller cleaning up after a failed RegisterReadChannel(4, ...)
        // when the failure mode is the per-id race (index 4 was rolled back but
        // _idToIndex still points at index 2).
        registry.UnregisterChannel(4, "shared-id");

        Assert.Same(legitimate, registry.GetReadChannel(2));
        Assert.Same(legitimate, registry.GetReadChannelById("shared-id"));
    }

    /// <summary>
    /// UnregisterChannel(index, channelId) with the channel's own (index, channelId)
    /// pair must still remove the ID mapping — the scoping fix from #228 must not
    /// regress the normal path.
    /// </summary>
    [Fact]
    public void UnregisterChannel_MatchingPair_RemovesIdMapping()
    {
        var registry = new ChannelRegistry(useOddIndices: false);

        var channel = new ReadChannel("own-id", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, channel);

        Assert.True(registry.UnregisterChannel(2, "own-id"));

        Assert.Null(registry.GetReadChannel(2));
        Assert.Null(registry.GetReadChannelById("own-id"));
    }

    /// <summary>
    /// Regression for #269. EnqueueForAccept loses the cancellation race against
    /// AcceptChannelAsync's ct.Register callback when both run after the dictionary
    /// TryRemove but before TrySetResult. The TCS in _pendingAccepts ends up in
    /// the Cancelled state, EnqueueForAccept's TrySetResult returns false, and the
    /// channel must NOT be silently dropped — it must be routed to _acceptQueue
    /// so the host can still observe it via AcceptChannelsAsync(null). We model
    /// this deterministically by pre-stuffing _pendingAccepts with an already-
    /// cancelled TCS (matching the post-race state) and calling EnqueueForAccept
    /// directly.
    /// </summary>
    [Fact]
    public async Task EnqueueForAccept_TcsAlreadyCancelled_RoutesChannelToAcceptQueue()
    {
        var registry = new ChannelRegistry(useOddIndices: false);
        var channel = new ReadChannel("race-target", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, channel);

        var pendingAcceptsField = typeof(ChannelRegistry).GetField(
            "_pendingAccepts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingAcceptsField);
        var pendingAccepts = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>>)pendingAcceptsField!.GetValue(registry)!;

        var deadTcs = new TaskCompletionSource<ReadChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        deadTcs.SetCanceled();
        pendingAccepts["race-target"] = deadTcs;

        registry.EnqueueForAccept(channel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = registry.AcceptChannelsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync(), "channel must arrive via the catch-all stream when the pending TCS was already cancelled");
        Assert.Same(channel, enumerator.Current);
    }

    /// <summary>
    /// Regression for #269 (cancellation side). When the awaiter's ct fires AFTER
    /// EnqueueForAccept has already won the dictionary TryRemove (and therefore
    /// removed the TCS from the dictionary), the cancellation callback's own
    /// TryRemove returns false. In that case it MUST NOT call TrySetCanceled
    /// on the TCS — doing so dead-locks EnqueueForAccept's TrySetResult and the
    /// channel is silently lost. We assert that an awaiter whose token is
    /// cancelled after the TCS has already been removed from _pendingAccepts
    /// does not transition the TCS to Cancelled.
    /// </summary>
    [Fact]
    public async Task AcceptChannelAsync_CancellationAfterTcsRemoved_DoesNotCancelTcs()
    {
        var registry = new ChannelRegistry(useOddIndices: false);
        using var cts = new CancellationTokenSource();

        var acceptTask = registry.AcceptChannelAsync("late-cancel", cts.Token).AsTask();
        Assert.False(acceptTask.IsCompleted);

        var pendingAcceptsField = typeof(ChannelRegistry).GetField(
            "_pendingAccepts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var pendingAccepts = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>>)pendingAcceptsField!.GetValue(registry)!;

        Assert.True(pendingAccepts.TryRemove("late-cancel", out var tcs), "EnqueueForAccept simulated: TCS removed from dictionary");

        cts.Cancel();
        await Task.Delay(50);

        Assert.False(tcs!.Task.IsCanceled, "cancellation callback must not flip the TCS once another path has already removed it");
        Assert.False(tcs.Task.IsCompleted);

        // EnqueueForAccept's TrySetResult should still succeed and complete the awaiter.
        var channel = new ReadChannel("late-cancel", 2, ChannelPriority.Normal, 64 * 1024);
        registry.RegisterReadChannel(2, channel);
        Assert.True(tcs.TrySetResult(channel));
        Assert.Same(channel, await acceptTask);
    }

    private sealed class NoopOwner : IChannelOwner
    {
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
    }
}
