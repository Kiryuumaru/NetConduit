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
    }
}
