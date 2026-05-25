using System.Collections.Concurrent;
using System.Threading.Channels;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

internal sealed class ChannelRegistry
{
    private readonly ConcurrentDictionary<ushort, WriteChannel> _writeChannels = new();
    private readonly ConcurrentDictionary<ushort, ReadChannel> _readChannels = new();
    private readonly ConcurrentDictionary<string, ushort> _idToIndex = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>> _pendingAccepts = new();
    private readonly ConcurrentDictionary<string, ReadChannel> _pendingAcceptChannels = new();
    private readonly Channel<ReadChannel> _acceptQueue = Channel.CreateUnbounded<ReadChannel>();
    private readonly object _prefixSubscriptionsLock = new();
    private readonly List<PrefixSubscription> _prefixSubscriptions = new();

    private sealed class PrefixSubscription(string prefix)
    {
        public string Prefix { get; } = prefix;
        public Channel<ReadChannel> Queue { get; } = Channel.CreateUnbounded<ReadChannel>();
    }

    /// <summary>
    /// Validates that <paramref name="candidate"/> does not collide with any
    /// existing subscription. Two prefixes collide when one is a prefix of the
    /// other (including equality): routing the inbound channel would otherwise
    /// be ambiguous. Caller MUST hold <see cref="_prefixSubscriptionsLock"/>.
    /// </summary>
    private void ThrowIfPrefixConflictsLocked(string candidate)
    {
        foreach (var existing in _prefixSubscriptions)
        {
            if (existing.Prefix.StartsWith(candidate, StringComparison.Ordinal) ||
                candidate.StartsWith(existing.Prefix, StringComparison.Ordinal))
            {
                throw new MultiplexerException(
                    ErrorCode.ChannelExists,
                    $"Prefix subscription '{candidate}' conflicts with existing subscription '{existing.Prefix}'. " +
                    "Each prefix namespace may have at most one active subscriber; " +
                    "the previous subscription must be cancelled before re-registering an overlapping prefix.");
            }
        }
    }

    /// <summary>
    /// Serializes the compound "register pending + read existing" sequences in
    /// AcceptChannel against the reader's "adopt pending + register read channel"
    /// sequence in DispatchToChannel(Init). Without this lock, the test can
    /// observe a transient registry state where _readChannels holds the reader's
    /// transient channel while _pendingAcceptChannels holds the caller's pending
    /// channel, causing the two sides to end up with different ReadChannel
    /// instances for the same channel id.
    /// </summary>
    internal readonly object AcceptLock = new();

    /// <summary>
    /// Serializes pre-handshake index allocation against the post-handshake
    /// parity reassignment walk. Held by:
    ///   1. OpenChannel / TryRegisterChannels around AllocateChannelIndex +
    ///      RegisterWriteChannel + WriteInitFrame, so the channel is fully
    ///      published (and its INIT frame stamped) before any concurrent
    ///      reassign walk takes a snapshot.
    ///   2. StreamMultiplexer.ReassignPreHandshakeWriteChannelIndices around
    ///      its GetAllWriteChannels enumeration + rekey loop.
    /// Without this lock there is a race: OpenChannel reads the pre-flip
    /// _nextChannelIndex, the main loop runs SetIndexParity + the reassign
    /// snapshot (missing the unregistered channel), then OpenChannel registers
    /// + writes INIT with the wrong-parity index → INIT-ACK collision and
    /// WriteChannel.WaitForReadyAsync hangs.
    /// </summary>
    internal readonly object ChannelIndexLock = new();

    private int _nextChannelIndex;
    private readonly int _indexStep = 2;

    internal ChannelRegistry(bool useOddIndices)
    {
        // Odd/even partitioning: one side uses odd, the other uses even indices
        _nextChannelIndex = useOddIndices ? 1 : 2;
    }

    internal void SetIndexParity(bool useOddIndices)
    {
        // Called after handshake determines which side gets odd vs even indices.
        // Only valid before any channels have been allocated.
        _nextChannelIndex = useOddIndices ? 1 : 2;
    }

    /// <summary>
    /// True when <paramref name="index"/> matches the current allocation parity
    /// (odd vs even). Used after the handshake's <see cref="SetIndexParity"/>
    /// to identify pre-handshake-allocated indices that landed on the wrong
    /// parity space and must be reassigned before any frame is transmitted
    /// </summary>
    internal bool IsCurrentParity(ushort index)
    {
        bool useOdd = (Volatile.Read(ref _nextChannelIndex) & 1) == 1;
        return ((index & 1) == 1) == useOdd;
    }

    /// <summary>
    /// Atomically rekey a registered <see cref="WriteChannel"/> from
    /// <paramref name="oldIndex"/> to <paramref name="newIndex"/>. Used by the
    /// post-handshake reassignment for pre-handshake-allocated channels whose
    /// indices need to move into the correct parity space.
    /// </summary>
    internal void RekeyWriteChannel(ushort oldIndex, ushort newIndex, WriteChannel channel)
    {
        if (oldIndex == newIndex) return;
        // Reserve the new slot first so a parallel allocation cannot land on it
        // between the remove and the add.
        if (!_writeChannels.TryAdd(newIndex, channel))
            throw new MultiplexerException(
                ErrorCode.Internal,
                $"Cannot rekey write channel '{channel.ChannelId}' to index {newIndex}: slot already occupied.");
        _writeChannels.TryRemove(new KeyValuePair<ushort, WriteChannel>(oldIndex, channel));
        _idToIndex[channel.ChannelId] = newIndex;
    }

    internal ushort AllocateChannelIndex()
    {
        int index = Interlocked.Add(ref _nextChannelIndex, _indexStep) - _indexStep;
        if (index > ChannelConstants.MaxDataChannel)
            throw new MultiplexerException(ErrorCode.Internal, "Channel index space exhausted.");
        return (ushort)index;
    }

    internal void RegisterWriteChannel(ushort index, WriteChannel channel)
    {
        if (!_writeChannels.TryAdd(index, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Write channel with index {index} already exists.");
        if (!_idToIndex.TryAdd(channel.ChannelId, index))
        {
            // Roll back the per-index insert so we do not leave an orphan
            // WriteChannel reachable via GetWriteChannel(index) /
            // GetAllWriteChannels() after the throw. The caller has no
            // way to undo this themselves: they never received a successful
            // return and have no record of the allocated index.
            _writeChannels.TryRemove(new KeyValuePair<ushort, WriteChannel>(index, channel));
            throw new MultiplexerException(ErrorCode.ChannelExists, $"A channel with ID '{channel.ChannelId}' already exists.");
        }
    }

    internal void RegisterReadChannel(ushort index, ReadChannel channel)
    {
        if (!_readChannels.TryAdd(index, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Read channel with index {index} already exists.");
        if (!_idToIndex.TryAdd(channel.ChannelId, index))
        {
            // Roll back the per-index insert.
            _readChannels.TryRemove(new KeyValuePair<ushort, ReadChannel>(index, channel));
            throw new MultiplexerException(ErrorCode.ChannelExists, $"A channel with ID '{channel.ChannelId}' already exists.");
        }
    }

    internal WriteChannel? GetWriteChannel(ushort index)
    {
        _writeChannels.TryGetValue(index, out var channel);
        return channel;
    }

    internal ReadChannel? GetReadChannel(ushort index)
    {
        _readChannels.TryGetValue(index, out var channel);
        return channel;
    }

    internal WriteChannel? GetWriteChannelById(string channelId)
    {
        if (_idToIndex.TryGetValue(channelId, out var index))
            return GetWriteChannel(index);
        return null;
    }

    internal ReadChannel? GetReadChannelById(string channelId)
    {
        if (_idToIndex.TryGetValue(channelId, out var index))
            return GetReadChannel(index);
        return null;
    }

    internal bool UnregisterChannel(ushort index, string channelId)
    {
        bool removed = _writeChannels.TryRemove(index, out _) || _readChannels.TryRemove(index, out _);
        // Scope the _idToIndex removal to the (channelId, index) pair so a mismatched
        // call — e.g. cleanup after a failed-to-commit registration where _idToIndex
        // still points at a *different* (legitimate) channel under the same ChannelId
        // — cannot tear down the legitimate mapping.
        _idToIndex.TryRemove(new KeyValuePair<string, ushort>(channelId, index));
        return removed;
    }

    internal IReadOnlyCollection<WriteChannel> GetAllWriteChannels() => _writeChannels.Values.ToArray();
    internal IReadOnlyCollection<ReadChannel> GetAllReadChannels() => _readChannels.Values.ToArray();

    // Accept coordination
    internal bool TryRegisterPendingAcceptChannel(string channelId, ReadChannel channel)
    {
        return _pendingAcceptChannels.TryAdd(channelId, channel);
    }

    internal ReadChannel? GetPendingAcceptChannel(string channelId)
    {
        _pendingAcceptChannels.TryGetValue(channelId, out var channel);
        return channel;
    }

    internal void RemovePendingAcceptChannel(string channelId)
    {
        _pendingAcceptChannels.TryRemove(channelId, out _);
    }

    internal void EnqueueForAccept(ReadChannel channel)
    {
        // Check if there's a specific async accept waiting for this channel ID.
        // If we win TryRemove but TrySetResult fails, the awaiter raced us to
        // cancellation between our TryRemove and our TrySetResult — its callback
        // unconditionally TrySetCanceled'd the same TCS instance after losing
        // its own TryRemove. The TCS is dead but the channel is still live and
        // fully registered in _readChannels: we MUST route it to the catch-all
        // _acceptQueue (or a matching prefix subscription) instead of dropping
        // it on the floor, or the peer keeps the channel open and writes pile
        // up unread in the orphan slab.
        if (_pendingAccepts.TryRemove(channel.ChannelId, out var tcs))
        {
            if (tcs.TrySetResult(channel))
                return;
            // TrySetResult failed: TCS was already completed. If it was
            // completed via AcceptChannelAsync's fast-path with THIS same
            // channel reference, the named waiter has already received it —
            // suppress to avoid double-delivery. Otherwise
            // (cancelled, or completed with a stale channel from a prior
            // close+reopen cycle of the same id), fall through so the new
            // channel is still delivered somewhere.
            if (tcs.Task.Status == TaskStatus.RanToCompletion &&
                ReferenceEquals(tcs.Task.Result, channel))
                return;
        }
        // Then check prefix subscriptions (overlay protocols claiming a namespace).
        // Prefixes are guaranteed non-overlapping by ThrowIfPrefixConflictsLocked,
        // so at most one subscription can match — iteration order does not matter.
        lock (_prefixSubscriptionsLock)
        {
            foreach (var sub in _prefixSubscriptions)
            {
                if (channel.ChannelId.StartsWith(sub.Prefix, StringComparison.Ordinal))
                {
                    sub.Queue.Writer.TryWrite(channel);
                    return;
                }
            }
        }
        // Otherwise queue for generic AcceptChannelsAsync
        _acceptQueue.Writer.TryWrite(channel);
    }

    internal async ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken ct)
    {
        // Register the TCS first. EnqueueForAccept treats a registered TCS as
        // the sole hand-off token: once present, an inbound INIT delivers the
        // channel via TrySetResult and never falls through to _acceptQueue.
        //
        // We must NOT return the channel from a fast-path _idToIndex lookup
        // without going through the TCS. If we did, a concurrent HandleInitFrame
        // could have just committed RegisterReadChannel (so _idToIndex hits)
        // but not yet called EnqueueForAccept. Returning directly from the
        // fast path would then let EnqueueForAccept fall through to
        // _acceptQueue, delivering the same ReadChannel to this caller AND
        // a generic AcceptChannelsAsync consumer.
        var tcs = new TaskCompletionSource<ReadChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAccepts[channelId] = tcs;

        // If the channel had already arrived before this call, complete the
        // TCS ourselves with the channel reference. A concurrent or subsequent
        // EnqueueForAccept(channel) will then find the TCS still in the
        // dictionary, TryRemove it, observe TrySetResult fails, and (because
        // the TCS already holds *this same channel*) suppress its queue
        // fall-through — preventing the double-delivery race.
        if (_idToIndex.TryGetValue(channelId, out var index))
        {
            var existing = GetReadChannel(index);
            if (existing is not null)
                tcs.TrySetResult(existing);
        }

        // On cancellation we must remove our entry from _pendingAccepts. Without
        // this, a cancelled accept leaves a permanently-cancelled TCS in the
        // dictionary; the next inbound INIT for the same channel ID gets routed
        // to that dead TCS in EnqueueForAccept (TryRemove succeeds, TrySetResult
        // is a no-op on a completed TCS) and the channel is silently dropped
        // from the accept stream.
        //
        // Only TrySetCanceled when we actually won the TryRemove. If we lost the
        // race to EnqueueForAccept, the TCS will be — or already is — completed
        // by it, and EnqueueForAccept's TrySetResult-fallback path takes
        // care of routing the channel to the catch-all queue when needed.
        await using var registration = ct.Register(() =>
        {
            if (_pendingAccepts.TryRemove(channelId, out _))
                tcs.TrySetCanceled(ct);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    internal IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(string? channelIdPrefix, CancellationToken ct)
    {
        if (channelIdPrefix is null)
            return _acceptQueue.Reader.ReadAllAsync(ct);

        PrefixSubscription sub;
        lock (_prefixSubscriptionsLock)
        {
            ThrowIfPrefixConflictsLocked(channelIdPrefix);
            sub = new PrefixSubscription(channelIdPrefix);
            _prefixSubscriptions.Add(sub);
        }
        // Wrap the iterator in an owner that releases the subscription on
        // ct cancellation, enumerator disposal, OR finalization. This avoids
        // the split-state lifecycle leak where the eager outer add was paired
        // with a release that only ran if iteration actually started.
        return new PrefixSubscriptionEnumerable(this, sub, ct);
    }

    /// <summary>
    /// Owns a registered <see cref="PrefixSubscription"/> on behalf of an
    /// <c>AcceptChannelsAsync(prefix, ct)</c> call. The subscription is
    /// guaranteed to be released exactly once via the first of: outer ct
    /// cancellation, enumerator <c>finally</c>, or finalizer (backstop when
    /// the enumerable is discarded without iteration AND ct is uncancellable).
    /// </summary>
    private sealed class PrefixSubscriptionEnumerable : IAsyncEnumerable<ReadChannel>
    {
        private readonly ChannelRegistry _registry;
        private readonly PrefixSubscription _sub;
        private CancellationTokenRegistration _ctRegistration;
        private int _released;

        public PrefixSubscriptionEnumerable(ChannelRegistry registry, PrefixSubscription sub, CancellationToken outerCt)
        {
            _registry = registry;
            _sub = sub;
            if (outerCt.CanBeCanceled)
            {
                _ctRegistration = outerCt.Register(
                    static state => ((PrefixSubscriptionEnumerable)state!).Release(),
                    this);
            }
        }

        ~PrefixSubscriptionEnumerable() => Release();

        public IAsyncEnumerator<ReadChannel> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        private async IAsyncEnumerable<ReadChannel> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await foreach (var channel in _sub.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    yield return channel;
            }
            finally
            {
                Release();
            }
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _ctRegistration.Dispose();
            _registry.RemovePrefixSubscription(_sub);
            GC.SuppressFinalize(this);
        }
    }

    private void RemovePrefixSubscription(PrefixSubscription sub)
    {
        bool removed;
        lock (_prefixSubscriptionsLock)
        {
            removed = _prefixSubscriptions.Remove(sub);
        }
        if (!removed) return;

        // Stop accepting new writes. Any concurrent EnqueueForAccept that already
        // observed sub before this lock-release will not happen: that path holds
        // _prefixSubscriptionsLock for its full foreach + TryWrite, so once
        // Remove() returns under the same lock, no further EnqueueForAccept can
        // pick this subscription.
        sub.Queue.Writer.TryComplete();

        // Re-route any channels that were buffered for this subscription but
        // never enumerated (e.g. cancelled before the consumer awaited them)
        // back into the default accept stream. Dropping them silently would
        // leak channel state on this side while the peer still believes the
        // channel is open.
        while (sub.Queue.Reader.TryRead(out var leftover))
            _acceptQueue.Writer.TryWrite(leftover);
    }

    internal void CancelAllPendingAccepts()
    {
        foreach (var kvp in _pendingAccepts)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingAccepts.Clear();
        _acceptQueue.Writer.TryComplete();
        lock (_prefixSubscriptionsLock)
        {
            foreach (var sub in _prefixSubscriptions)
                sub.Queue.Writer.TryComplete();
            _prefixSubscriptions.Clear();
        }
    }

    internal void AbortAllChannels(ChannelCloseReason reason, Exception? exception = null)
    {
        // Teardown discipline: every registered channel that was ever connected
        // must fire Disconnected before Closed (fixes #391). The natural
        // transport-drop path in MainLoopAsync already calls MarkDisconnected
        // explicitly; the mux-dispose, local-GoAway, remote-GoAway, and
        // terminal-failure paths all funnel through AbortAllChannels and used
        // to skip it, violating IChannel.Disconnected's documented contract.
        // MarkDisconnected is idempotent (no-op on never-connected channels and
        // on channels already disconnected this cycle), so the natural-drop
        // path's explicit pre-call still suppresses a duplicate fire here.
        DisconnectReason disconnectReason = reason switch
        {
            ChannelCloseReason.MuxDisposed => DisconnectReason.LocalDispose,
            ChannelCloseReason.TransportFailed => DisconnectReason.TransportError,
            _ => DisconnectReason.LocalDispose,
        };
        foreach (var channel in _writeChannels.Values)
            channel.MarkDisconnected(disconnectReason, exception);
        foreach (var channel in _readChannels.Values)
            channel.MarkDisconnected(disconnectReason, exception);
        // Pending accepts were never connected — no Disconnected event to fire.

        foreach (var channel in _writeChannels.Values)
            channel.SetClosed(reason, exception);
        foreach (var channel in _readChannels.Values)
            channel.SetClosed(reason, exception);
        foreach (var channel in _pendingAcceptChannels.Values)
            channel.SetClosed(reason, exception);
        _writeChannels.Clear();
        _readChannels.Clear();
        _pendingAcceptChannels.Clear();
        _idToIndex.Clear();
    }
}
