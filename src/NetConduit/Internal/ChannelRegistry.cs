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
        // Check if there's a pre-created pending accept channel for this ID
        if (_pendingAcceptChannels.TryRemove(channel.ChannelId, out var pendingChannel))
        {
            if (ReferenceEquals(pendingChannel, channel))
            {
                // DispatchToChannel already found and used the pending channel directly. Just clean up.
                return;
            }

            // Race: DispatchToChannel created 'channel' but user holds 'pendingChannel' via AcceptChannel(sync).
            // Replace the registration so future data flows to the pending channel.
            _readChannels[channel.ChannelIndex] = pendingChannel;
            pendingChannel.SetChannelIndex(channel.ChannelIndex);
            pendingChannel.MarkOpen();
            pendingChannel.MarkConnected();
            return;
        }

        // Check if there's a specific async accept waiting for this channel ID
        if (_pendingAccepts.TryRemove(channel.ChannelId, out var tcs))
        {
            tcs.TrySetResult(channel);
            return;
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

        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    internal IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken ct)
    {
        return _acceptQueue.Reader.ReadAllAsync(ct);
    }

    internal void CancelAllPendingAccepts()
    {
        foreach (var kvp in _pendingAccepts)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingAccepts.Clear();
        _acceptQueue.Writer.TryComplete();
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
