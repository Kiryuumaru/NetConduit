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
            throw new MultiplexerException(ErrorCode.ChannelExists, $"A channel with ID '{channel.ChannelId}' already exists.");
    }

    internal void RegisterReadChannel(ushort index, ReadChannel channel)
    {
        if (!_readChannels.TryAdd(index, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Read channel with index {index} already exists.");
        if (!_idToIndex.TryAdd(channel.ChannelId, index))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"A channel with ID '{channel.ChannelId}' already exists.");
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
        _idToIndex.TryRemove(channelId, out _);
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
        // Check if there's a specific async accept waiting for this channel ID
        if (_pendingAccepts.TryRemove(channel.ChannelId, out var tcs))
        {
            tcs.TrySetResult(channel);
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
        // Check if channel already arrived
        if (_idToIndex.TryGetValue(channelId, out var index))
        {
            var existing = GetReadChannel(index);
            if (existing is not null) return existing;
        }

        var tcs = new TaskCompletionSource<ReadChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAccepts[channelId] = tcs;

        // Re-check after registration — channel may have arrived between first check and registration
        if (_idToIndex.TryGetValue(channelId, out index))
        {
            var existing = GetReadChannel(index);
            if (existing is not null)
            {
                _pendingAccepts.TryRemove(channelId, out _);
                return existing;
            }
        }

        // On cancellation we must remove our entry from _pendingAccepts. Without
        // this, a cancelled accept leaves a permanently-cancelled TCS in the
        // dictionary; the next inbound INIT for the same channel ID gets routed
        // to that dead TCS in EnqueueForAccept (TryRemove succeeds, TrySetResult
        // is a no-op on a completed TCS) and the channel is silently dropped
        // from the accept stream.
        await using var registration = ct.Register(() =>
        {
            _pendingAccepts.TryRemove(channelId, out _);
            tcs.TrySetCanceled(ct);
        });
        return await tcs.Task;
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
        return EnumerateAndCleanupSubscriptionAsync(sub, ct);
    }

    /// <summary>
    /// Bound the lifetime of <paramref name="sub"/> to a single enumeration:
    /// when the consumer cancels <paramref name="ct"/> or disposes the
    /// enumerator, the subscription is removed from the dispatch list, its
    /// queue is closed, and any matching channels that were buffered but
    /// never consumed are re-routed to the default accept stream so the host
    /// application can observe them rather than have them silently dropped.
    /// </summary>
    private async IAsyncEnumerable<ReadChannel> EnumerateAndCleanupSubscriptionAsync(
        PrefixSubscription sub,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var channel in sub.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return channel;
        }
        finally
        {
            RemovePrefixSubscription(sub);
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
