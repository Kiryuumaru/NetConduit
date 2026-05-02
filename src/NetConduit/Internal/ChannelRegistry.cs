using System.Collections.Concurrent;
using System.Threading.Channels;
using NetConduit.Constants;

namespace NetConduit.Internal;

internal sealed class ChannelRegistry
{
    private readonly ConcurrentDictionary<ushort, WriteChannel> _writeChannels = new();
    private readonly ConcurrentDictionary<ushort, ReadChannel> _readChannels = new();
    private readonly ConcurrentDictionary<string, ushort> _idToIndex = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>> _pendingAccepts = new();
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
        _idToIndex.TryAdd(channel.ChannelId, index);
    }

    internal void RegisterReadChannel(ushort index, ReadChannel channel)
    {
        if (!_readChannels.TryAdd(index, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Read channel with index {index} already exists.");
        _idToIndex.TryAdd(channel.ChannelId, index);
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

    internal void UnregisterChannel(ushort index, string channelId)
    {
        _writeChannels.TryRemove(index, out _);
        _readChannels.TryRemove(index, out _);
        _idToIndex.TryRemove(channelId, out _);
    }

    internal IReadOnlyCollection<WriteChannel> GetAllWriteChannels() => _writeChannels.Values.ToArray();
    internal IReadOnlyCollection<ReadChannel> GetAllReadChannels() => _readChannels.Values.ToArray();

    // Accept coordination
    internal void EnqueueForAccept(ReadChannel channel)
    {
        // Check if there's a specific accept waiting for this channel ID
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
    }
}
