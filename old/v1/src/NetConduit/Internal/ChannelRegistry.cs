using System.Collections.Concurrent;
using System.Threading.Channels;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;

namespace NetConduit.Internal;

internal sealed class ChannelRegistry
{
    private readonly ConcurrentDictionary<uint, WriteChannel> _writeChannelsByIndex = new();
    private readonly ConcurrentDictionary<uint, ReadChannel> _readChannelsByIndex = new();
    private readonly ConcurrentDictionary<string, WriteChannel> _writeChannelsById = new();
    private readonly ConcurrentDictionary<string, ReadChannel> _readChannelsById = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>> _pendingAccepts = new();
    private readonly Channel<ReadChannel> _acceptChannel;

    private long _localNonce;
    private long _remoteNonce;
    private bool _indexSpaceDetermined;
    private uint _nextChannelIndex;

    internal ChannelRegistry()
    {
        _acceptChannel = Channel.CreateUnbounded<ReadChannel>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    }

    internal long LocalNonce { get => _localNonce; set => _localNonce = value; }
    internal long RemoteNonce { get => _remoteNonce; set => _remoteNonce = value; }
    internal bool IndexSpaceDetermined => _indexSpaceDetermined;
    internal uint NextChannelIndex => Volatile.Read(ref _nextChannelIndex);

    internal void DetermineIndexSpace(Guid sessionId, Guid remoteSessionId)
    {
        if (_indexSpaceDetermined)
            return;

        bool useOddIndices;
        if (_localNonce != _remoteNonce)
        {
            useOddIndices = _localNonce > _remoteNonce;
        }
        else
        {
            useOddIndices = sessionId.CompareTo(remoteSessionId) > 0;
        }

        _nextChannelIndex = useOddIndices ? 1u : 2u;
        _indexSpaceDetermined = true;
    }

    internal uint AllocateChannelIndex()
    {
        if (!_indexSpaceDetermined)
            throw new InvalidOperationException("Cannot allocate channel index before handshake completes.");

        var id = Interlocked.Add(ref _nextChannelIndex, 2) - 2;

        if (id > ChannelIndexLimits.MaxDataChannel)
            throw new MultiplexerException(ErrorCode.Internal, "Channel index space exhausted.");

        return id;
    }

    // Write channel operations
    internal bool TryAddWriteChannel(uint index, WriteChannel channel) => _writeChannelsByIndex.TryAdd(index, channel);
    internal bool TryAddWriteChannelById(string id, WriteChannel channel) => _writeChannelsById.TryAdd(id, channel);
    internal void RemoveWriteChannel(uint index, string id)
    {
        _writeChannelsByIndex.TryRemove(index, out _);
        _writeChannelsById.TryRemove(id, out _);
    }

    internal WriteChannel? GetWriteChannelByIndex(uint index) =>
        _writeChannelsByIndex.TryGetValue(index, out var c) ? c : null;

    internal WriteChannel? GetWriteChannelById(string id) =>
        _writeChannelsById.TryGetValue(id, out var c) ? c : null;

    internal bool ContainsWriteChannelById(string id) => _writeChannelsById.ContainsKey(id);
    internal ICollection<WriteChannel> WriteChannels => _writeChannelsByIndex.Values;
    internal int WriteChannelCount => _writeChannelsByIndex.Count;

    // Read channel operations
    internal bool TryAddReadChannel(uint index, ReadChannel channel) => _readChannelsByIndex.TryAdd(index, channel);
    internal bool TryAddReadChannelById(string id, ReadChannel channel) => _readChannelsById.TryAdd(id, channel);
    internal void RemoveReadChannel(uint index, string id)
    {
        _readChannelsByIndex.TryRemove(index, out _);
        _readChannelsById.TryRemove(id, out _);
    }

    internal ReadChannel? GetReadChannelByIndex(uint index) =>
        _readChannelsByIndex.TryGetValue(index, out var c) ? c : null;

    internal ReadChannel? GetReadChannelById(string id) =>
        _readChannelsById.TryGetValue(id, out var c) ? c : null;

    internal bool ContainsReadChannelById(string id) => _readChannelsById.ContainsKey(id);
    internal ICollection<ReadChannel> ReadChannels => _readChannelsByIndex.Values;
    internal int ReadChannelCount => _readChannelsByIndex.Count;

    // Accept coordination
    internal bool TryAddPendingAccept(string id, TaskCompletionSource<ReadChannel> tcs) => _pendingAccepts.TryAdd(id, tcs);

    internal bool TryGetPendingAccept(string id, out TaskCompletionSource<ReadChannel>? tcs) =>
        _pendingAccepts.TryGetValue(id, out tcs);

    internal bool TryRemovePendingAccept(string id, out TaskCompletionSource<ReadChannel>? tcs) =>
        _pendingAccepts.TryRemove(id, out tcs);

    internal void WriteAcceptChannel(ReadChannel channel) => _acceptChannel.Writer.TryWrite(channel);
    internal IAsyncEnumerable<ReadChannel> ReadAllAcceptedAsync(CancellationToken ct) => _acceptChannel.Reader.ReadAllAsync(ct);
    internal void CompleteAcceptChannel() => _acceptChannel.Writer.TryComplete();

    // Abort and cleanup
    internal void AbortAllChannels(ChannelCloseReason reason, Exception? ex)
    {
        foreach (var channel in _writeChannelsByIndex.Values)
        {
            channel.Abort(reason, ex);
        }
        foreach (var channel in _readChannelsByIndex.Values)
        {
            channel.Abort(reason, ex);
        }
    }

    internal async Task DisposeAllChannelsAsync(TimeSpan timeout)
    {
        var tasks = new List<Task>();

        foreach (var channel in _writeChannelsByIndex.Values.ToArray())
        {
            tasks.Add(channel.DisposeAsync().AsTask());
        }

        foreach (var channel in _readChannelsByIndex.Values.ToArray())
        {
            tasks.Add(channel.DisposeAsync().AsTask());
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(tasks),
                Task.Delay(timeout)
            ).ConfigureAwait(false);
        }
    }

    internal void CancelPendingAccepts()
    {
        foreach (var tcs in _pendingAccepts.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingAccepts.Clear();
    }

    // Queries
    internal IReadOnlyCollection<string> ActiveChannelIds
    {
        get
        {
            var ids = new HashSet<string>(_writeChannelsById.Keys);
            foreach (var id in _readChannelsById.Keys)
                ids.Add(id);
            return ids;
        }
    }

    internal IReadOnlyCollection<string> OpenedChannelIds => _writeChannelsById.Keys.ToArray();
    internal IReadOnlyCollection<string> AcceptedChannelIds => _readChannelsById.Keys.ToArray();
    internal int ActiveChannelCount => _writeChannelsByIndex.Count + _readChannelsByIndex.Count;

    // Iteration for reconnection protocol
    internal IEnumerable<KeyValuePair<uint, WriteChannel>> WriteChannelEntries => _writeChannelsByIndex;
    internal IEnumerable<KeyValuePair<uint, ReadChannel>> ReadChannelEntries => _readChannelsByIndex;
}
