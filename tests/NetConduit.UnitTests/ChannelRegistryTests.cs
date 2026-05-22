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
