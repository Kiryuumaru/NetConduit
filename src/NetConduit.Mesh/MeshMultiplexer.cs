using System.Collections.Concurrent;
using System.Threading.Channels;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Mesh.Events;
using NetConduit.Mesh.Interfaces;
using NetConduit.Mesh.Internal;
using NetConduit.Models;
using CoreErrorEventArgs = NetConduit.Events.ErrorEventArgs;

namespace NetConduit.Mesh;

/// <summary>
/// Multi-hop routing layer on top of <see cref="IStreamMultiplexer"/>.
/// </summary>
public sealed class MeshMultiplexer : IMeshMultiplexer
{
    private readonly MeshMultiplexerOptions _options;
    private readonly MeshStats _stats = new();

    private readonly object _stateLock = new();
    private readonly Dictionary<string, NeighborSession> _neighbors = new(StringComparer.Ordinal);
    private readonly AdjacencyMap _map = new();
    private readonly HashSet<string> _reachable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteHop> _routes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<OpenerKey, OpenerSession> _openers = new();
    private readonly ConcurrentDictionary<AcceptorKey, AcceptorSession> _acceptors = new();
    private readonly Channel<RoutedMultiplexer> _inbox = Channel.CreateUnbounded<RoutedMultiplexer>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    private long _localVersion;
    private long _nonceSequence;
    private volatile bool _isRunning;
    private volatile bool _isReady;
    private volatile bool _isShuttingDown;
    private volatile bool _isDisposed;
    private int _activeRelaySlots;

    /// <inheritdoc />
    public string NodeId => _options.NodeId;

    /// <inheritdoc />
    public string? PoolId => _options.PoolId;

    /// <inheritdoc />
    public bool IsReady => _isReady;

    /// <inheritdoc />
    public bool IsRunning => _isRunning && !_isDisposed;

    /// <inheritdoc />
    public int ReachableNodeCount
    {
        get
        {
            lock (_stateLock)
            {
                return _reachable.Count;
            }
        }
    }

    /// <inheritdoc />
    public int KnownNodeCount
    {
        get
        {
            lock (_stateLock)
            {
                // Map.Count includes self entry; subtract 1 to match "known remote nodes".
                int total = _map.Count;
                return total > 0 ? total - 1 : 0;
            }
        }
    }

    /// <inheritdoc />
    public MeshStats Stats => _stats;

    /// <inheritdoc />
    public event EventHandler? Ready;
    /// <inheritdoc />
    public event EventHandler<NodeReachableEventArgs>? NodeReachable;
    /// <inheritdoc />
    public event EventHandler<NodeUnreachableEventArgs>? NodeUnreachable;
    /// <inheritdoc />
    public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
    /// <inheritdoc />
    public event EventHandler<CoreErrorEventArgs>? Error;

    private MeshMultiplexer(MeshMultiplexerOptions options)
    {
        _options = options;
    }

    /// <summary>Create a new mesh multiplexer with the given options.</summary>
    public static MeshMultiplexer Create(MeshMultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new MeshMultiplexer(options);
    }

    /// <summary>Start the mesh. Begins accept loops on registered neighbors as they are added.</summary>
    public void Start()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
        }
        if (_isRunning)
        {
            throw new InvalidOperationException("Mesh multiplexer is already running.");
        }
        _isRunning = true;

        lock (_stateLock)
        {
            // Seed adjacency with our own entry (version 0, no neighbors yet).
            _localVersion = 1;
            _map.Apply(_options.NodeId, _localVersion, _options.PoolId, Array.Empty<string>());
        }
    }

    /// <inheritdoc />
    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
        }
        return _readyTcs.Task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public void AddNeighbor(string remoteNodeId, IStreamMultiplexer mux, string? remotePoolId = null)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
        }
        if (!_isRunning)
        {
            throw new InvalidOperationException("Mesh must be started before adding neighbors.");
        }
        Identifiers.ValidateNodeId(remoteNodeId, nameof(remoteNodeId));
        if (remotePoolId is not null)
        {
            Identifiers.ValidatePoolId(remotePoolId, nameof(remotePoolId));
        }
        if (string.Equals(remoteNodeId, _options.NodeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Neighbor node ID must differ from local node ID.", nameof(remoteNodeId));
        }
        ArgumentNullException.ThrowIfNull(mux);

        NeighborSession session;
        lock (_stateLock)
        {
            if (_neighbors.ContainsKey(remoteNodeId))
            {
                throw new InvalidOperationException(
                    $"Neighbor '{remoteNodeId}' is already registered. Remove it first.");
            }

            // Update local entry: add neighbor, bump version.
            var selfEntry = GetOrCreateSelfEntry();
            var newNeighbors = new HashSet<string>(selfEntry.Neighbors, StringComparer.Ordinal) { remoteNodeId };
            _localVersion++;
            _map.Apply(_options.NodeId, _localVersion, _options.PoolId, newNeighbors.ToArray());

            // Seed neighbor with at least a stub entry so it can be picked up by BFS once
            // bidirectional confirmation lands via topology exchange.
            if (!_map.TryGet(remoteNodeId, out _))
            {
                _map.Apply(remoteNodeId, 0, remotePoolId, Array.Empty<string>());
            }

            session = new NeighborSession(this, remoteNodeId, mux, remotePoolId);
            _neighbors[remoteNodeId] = session;

            RecomputeRoutesUnderLock();
        }

        session.Start(_cts.Token);
        RaiseTopologyChanged();
    }

    /// <inheritdoc />
    public void RemoveNeighbor(string remoteNodeId)
    {
        if (_isDisposed)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(remoteNodeId);

        NeighborSession? session;
        lock (_stateLock)
        {
            if (!_neighbors.Remove(remoteNodeId, out session))
            {
                return;
            }

            var selfEntry = GetOrCreateSelfEntry();
            if (selfEntry.Neighbors.Remove(remoteNodeId))
            {
                _localVersion++;
                _map.Apply(_options.NodeId, _localVersion, _options.PoolId, selfEntry.Neighbors.ToArray());
            }

            RecomputeRoutesUnderLock();
        }

        _ = session!.DisposeAsync();
        BroadcastLocalTopology();
        RaiseTopologyChanged();
    }

    /// <inheritdoc />
    public IStreamMultiplexer OpenMultiplexer(string targetNodeId, string multiplexerId)
    {
        EnsureRunning();
        Identifiers.ValidateNodeId(targetNodeId, nameof(targetNodeId));
        Identifiers.ValidateMultiplexerId(multiplexerId, nameof(multiplexerId));
        if (string.Equals(targetNodeId, _options.NodeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Cannot open a routed mux to the local node.", nameof(targetNodeId));
        }

        var key = new OpenerKey(targetNodeId, multiplexerId);
        var opener = new OpenerSession(this, targetNodeId, multiplexerId);
        if (!_openers.TryAdd(key, opener))
        {
            throw new InvalidOperationException(
                $"A routed multiplexer to '{targetNodeId}' with ID '{multiplexerId}' is already open.");
        }

        try
        {
            opener.Construct();
        }
        catch
        {
            _openers.TryRemove(key, out _);
            throw;
        }

        _stats.IncrementSubMultiplexers();
        _stats.IncrementRoutesOpened();
        return opener.SubMultiplexer;
    }

    /// <inheritdoc />
    public async Task<IStreamMultiplexer> OpenMultiplexerAsync(string targetNodeId, string multiplexerId, CancellationToken ct = default)
    {
        var mux = OpenMultiplexer(targetNodeId, multiplexerId);
        try
        {
            await mux.WaitForReadyAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await mux.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return mux;
    }

    /// <inheritdoc />
    public IStreamMultiplexer AcceptMultiplexer(string sourceNodeId, string multiplexerId)
    {
        EnsureRunning();
        Identifiers.ValidateNodeId(sourceNodeId, nameof(sourceNodeId));
        Identifiers.ValidateMultiplexerId(multiplexerId, nameof(multiplexerId));
        if (string.Equals(sourceNodeId, _options.NodeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Cannot accept a routed mux from the local node.", nameof(sourceNodeId));
        }

        var key = new AcceptorKey(sourceNodeId, multiplexerId);
        var acceptor = _acceptors.GetOrAdd(key, k => new AcceptorSession(this, k.SourceNodeId, k.MultiplexerId, isExplicit: true));
        acceptor.MarkExplicit();
        if (acceptor.SubMultiplexer is null)
        {
            acceptor.Construct();
        }
        return acceptor.SubMultiplexer ?? throw new InvalidOperationException("Acceptor sub-mux not constructed.");
    }

    /// <inheritdoc />
    public async Task<IStreamMultiplexer> AcceptMultiplexerAsync(string sourceNodeId, string multiplexerId, CancellationToken ct = default)
    {
        var mux = AcceptMultiplexer(sourceNodeId, multiplexerId);
        await mux.WaitForReadyAsync(ct).ConfigureAwait(false);
        return mux;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RoutedMultiplexer> AcceptMultiplexersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureRunning();
        var reader = _inbox.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var routed))
            {
                yield return routed;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask GoAwayAsync(CancellationToken ct = default)
    {
        if (_isDisposed || _isShuttingDown)
        {
            return;
        }
        _isShuttingDown = true;

        // Snapshot collections under lock then act outside it.
        NeighborSession[] neighbors;
        OpenerSession[] openers;
        AcceptorSession[] acceptors;
        lock (_stateLock)
        {
            neighbors = _neighbors.Values.ToArray();
        }
        openers = _openers.Values.ToArray();
        acceptors = _acceptors.Values.ToArray();

        // Drain inbox so any waiting AcceptMultiplexersAsync iterators unblock.
        _inbox.Writer.TryComplete();

        // GoAway sub-muxes (opener + acceptor). Use the configured GoAwayTimeout on each.
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.GoAwayTimeout);
        try
        {
            var goAwayTasks = new List<Task>();
            foreach (var o in openers)
            {
                goAwayTasks.Add(o.GoAwayAsync(timeoutCts.Token));
            }
            foreach (var a in acceptors)
            {
                goAwayTasks.Add(a.GoAwayAsync(timeoutCts.Token));
            }
            await Task.WhenAll(goAwayTasks).ConfigureAwait(false);
        }
        catch
        {
            // GoAway is best-effort.
        }
        finally
        {
            timeoutCts.Dispose();
        }

        foreach (var n in neighbors)
        {
            try
            {
                await n.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseError(ex);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        try
        {
            await GoAwayAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        foreach (var opener in _openers.Values)
        {
            try { await opener.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _openers.Clear();

        foreach (var acceptor in _acceptors.Values)
        {
            try { await acceptor.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _acceptors.Clear();

        _cts.Dispose();
        _readyTcs.TrySetCanceled();
    }

    // ----- Internal coordination -----

    private void EnsureRunning()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
        }
        if (!_isRunning)
        {
            throw new InvalidOperationException("Mesh multiplexer has not been started.");
        }
    }

    private NodeEntry GetOrCreateSelfEntry()
    {
        if (!_map.TryGet(_options.NodeId, out var entry))
        {
            _map.Apply(_options.NodeId, _localVersion, _options.PoolId, Array.Empty<string>());
            _map.TryGet(_options.NodeId, out entry);
        }
        return entry!;
    }

    private void RecomputeRoutesUnderLock()
    {
        var snapshot = _map.CreateSnapshot();
        var newRoutes = BfsRouter.ComputeRoutes(snapshot, _options.NodeId, _options.MaxHops);

        var prevReachable = new HashSet<string>(_reachable, StringComparer.Ordinal);
        _reachable.Clear();
        _routes.Clear();
        foreach (var (target, hop) in newRoutes)
        {
            _reachable.Add(target);
            _routes[target] = hop;
        }

        var toRaiseReachable = new List<(string node, string? pool, int hops)>();
        var toRaiseUnreachable = new List<string>();

        foreach (var (target, hop) in newRoutes)
        {
            if (!prevReachable.Contains(target))
            {
                toRaiseReachable.Add((target, snapshot.PoolOf(target), hop.HopCount));
            }
        }
        foreach (string old in prevReachable)
        {
            if (!_reachable.Contains(old))
            {
                toRaiseUnreachable.Add(old);
            }
        }

        // Defer event raising to outside the lock via Task.Run-style enqueue.
        if (toRaiseReachable.Count > 0 || toRaiseUnreachable.Count > 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (self, reachable, unreachable) = state;
                foreach (var (node, pool, hops) in reachable)
                {
                    self.RaiseEvent(self.NodeReachable, new NodeReachableEventArgs(node, pool, hops));
                }
                foreach (var node in unreachable)
                {
                    self.RaiseEvent(self.NodeUnreachable, new NodeUnreachableEventArgs(node));
                }
            }, (this, toRaiseReachable, toRaiseUnreachable), preferLocal: false);
        }
    }

    internal bool TryGetRoute(string targetNodeId, out string nextHopNodeId, out int hopCount)
    {
        lock (_stateLock)
        {
            if (_routes.TryGetValue(targetNodeId, out var hop))
            {
                nextHopNodeId = hop.NextHopNodeId;
                hopCount = hop.HopCount;
                return true;
            }
        }
        nextHopNodeId = string.Empty;
        hopCount = 0;
        return false;
    }

    internal bool TryGetNeighbor(string nodeId, out NeighborSession session)
    {
        lock (_stateLock)
        {
            if (_neighbors.TryGetValue(nodeId, out var s))
            {
                session = s;
                return true;
            }
        }
        session = null!;
        return false;
    }

    internal long NextNonce() => Interlocked.Increment(ref _nonceSequence);

    internal MeshMultiplexerOptions Options => _options;

    internal CancellationToken ShutdownToken => _cts.Token;

    internal void OnTopologyMessageReceived(IReadOnlyCollection<TopologyEntry> entries)
    {
        _stats.IncrementTopologyReceived();

        bool changed = false;
        lock (_stateLock)
        {
            foreach (var entry in entries)
            {
                // Reject self-shadow: only the owning node may advertise its own state.
                if (string.Equals(entry.NodeId, _options.NodeId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (_map.Apply(entry.NodeId, entry.Version, entry.PoolId, entry.Neighbors))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                RecomputeRoutesUnderLock();
            }
        }

        if (changed)
        {
            BroadcastLocalTopology();
            RaiseTopologyChanged();
        }

        // The mesh becomes "ready" once the first topology exchange has completed
        // (regardless of whether it changed our map).
        if (!_isReady)
        {
            _isReady = true;
            _readyTcs.TrySetResult();
            RaiseEvent(Ready, EventArgs.Empty);
        }
    }

    internal void BroadcastLocalTopology()
    {
        NeighborSession[] sessions;
        TopologyEntry[] entries;
        lock (_stateLock)
        {
            sessions = _neighbors.Values.ToArray();
            entries = SnapshotEntriesUnderLock();
        }
        foreach (var s in sessions)
        {
            s.SendTopology(entries);
        }
    }

    private TopologyEntry[] SnapshotEntriesUnderLock()
    {
        var result = new List<TopologyEntry>(_map.Count);
        foreach (string nodeId in _map.Nodes)
        {
            if (_map.TryGet(nodeId, out var entry))
            {
                result.Add(new TopologyEntry(
                    nodeId,
                    entry.Version,
                    entry.PoolId,
                    entry.Neighbors.ToArray()));
            }
        }
        return result.ToArray();
    }

    internal TopologyEntry[] SnapshotLocalEntries()
    {
        lock (_stateLock)
        {
            return SnapshotEntriesUnderLock();
        }
    }

    internal bool TryReserveRelaySlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeRelaySlots);
            if (current >= _options.MaxConcurrentRelays)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _activeRelaySlots, current + 1, current) == current)
            {
                _stats.IncrementRelays();
                return true;
            }
        }
    }

    internal void ReleaseRelaySlot()
    {
        Interlocked.Decrement(ref _activeRelaySlots);
        _stats.DecrementRelays();
    }

    internal void OnRelayBytesForwarded(long count)
    {
        _stats.AddRelayBytes(count);
    }

    internal void OnSubMultiplexerClosed()
    {
        _stats.DecrementSubMultiplexers();
    }

    internal void RemoveOpener(string targetNodeId, string multiplexerId)
    {
        _openers.TryRemove(new OpenerKey(targetNodeId, multiplexerId), out _);
    }

    internal void RemoveAcceptor(string sourceNodeId, string multiplexerId)
    {
        _acceptors.TryRemove(new AcceptorKey(sourceNodeId, multiplexerId), out _);
    }

    internal void OnRouteFailed()
    {
        _stats.IncrementRoutesFailed();
    }

    internal void OnRouteSucceeded()
    {
        _stats.IncrementRoutesOpened();
    }

    internal void OnTopologySent()
    {
        _stats.IncrementTopologySent();
    }

    internal Task DispatchInboundRouteAsync(string sourceNodeId, string multiplexerId, IReadChannel reader, IWriteChannel writer)
    {
        var key = new AcceptorKey(sourceNodeId, multiplexerId);
        var acceptor = _acceptors.GetOrAdd(key, k => new AcceptorSession(this, k.SourceNodeId, k.MultiplexerId, isExplicit: false));
        return acceptor.OnIncomingRouteAsync(reader, writer, _inbox.Writer);
    }

    internal void RaiseError(Exception ex)
    {
        try
        {
            Error?.Invoke(this, new CoreErrorEventArgs(ex));
        }
        catch
        {
            // Swallow handler errors to keep the mesh alive.
        }
    }

    internal void RaiseEvent<TArgs>(EventHandler<TArgs>? handler, TArgs args) where TArgs : EventArgs
    {
        if (handler is null) return;
        try { handler(this, args); } catch (Exception ex) { RaiseError(ex); }
    }

    internal void RaiseEvent(EventHandler? handler, EventArgs args)
    {
        if (handler is null) return;
        try { handler(this, args); } catch (Exception ex) { RaiseError(ex); }
    }

    private void RaiseTopologyChanged()
    {
        int known;
        int reachable;
        lock (_stateLock)
        {
            known = _map.Count > 0 ? _map.Count - 1 : 0;
            reachable = _reachable.Count;
        }
        RaiseEvent(TopologyChanged, new TopologyChangedEventArgs(known, reachable));
    }

    private readonly record struct OpenerKey(string TargetNodeId, string MultiplexerId);
    private readonly record struct AcceptorKey(string SourceNodeId, string MultiplexerId);
}
