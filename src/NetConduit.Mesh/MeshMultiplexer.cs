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

    // Cached reachability snapshot keyed by remote node ID. Mirrors `_reachable` /
    // `_routes` but carries the full (node, pool, hopCount) tuple so GetReachable
    // can serve a race-free snapshot without re-querying the adjacency map.
    private readonly Dictionary<string, NodeReachableEventArgs> _reachabilityInfo
        = new(StringComparer.Ordinal);

    // One-shot waiters keyed by node ID. Completed under `_stateLock` whenever a
    // node transitions to reachable, before the public NodeReachable event fires.
    // Subscribe-then-check on the event has an inherent gap; waiters parked here
    // are race-free with respect to the route recompute.
    private readonly Dictionary<string, List<TaskCompletionSource<NodeReachableEventArgs>>>
        _reachableWaiters = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<OpenerKey, OpenerSession> _openers = new();
    private readonly ConcurrentDictionary<AcceptorKey, AcceptorSession> _acceptors = new();
    private readonly Channel<RoutedMultiplexer> _inbox = Channel.CreateUnbounded<RoutedMultiplexer>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    // In-flight neighbor-session disposals from RemoveNeighbor /
    // HandleNeighborMuxDead. Those paths fire-and-forget the async teardown; tracking
    // the tasks here lets DisposeAsync drain them and lets RemoveNeighborAsync await
    // its own disposal without exposing internal session types.
    private readonly ConcurrentDictionary<Task, byte> _pendingNeighborDisposals = new();

    // In-flight routed-session closes triggered by inner-mux terminal Disconnected
    // events. The event handler runs synchronously on the publisher thread; the
    // actual cleanup is async, so the resulting task is parked here and drained on
    // mesh dispose. Without this, ChurnTests-style open/dispose loops leak relay
    // teardown work past test boundaries and pollute subsequent tests.
    private readonly ConcurrentDictionary<Task, byte> _pendingSessionCloses = new();

    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    // Event-driven route signaling. Bumped (under _stateLock) every time the route
    // table changes; pending openers WaitForChangeAsync on this version.
    private long _routeVersion;
    private TaskCompletionSource _routeChange = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Debounced topology recompute. Set when topology changes; the recompute timer
    // drains it. _recomputePending is the single-flight gate.
    private int _recomputePending;
    private Timer? _recomputeTimer;
    private Timer? _antiEntropyTimer;

    // Monotonic version assigned to each NeighborSession when created.
    // HandleNeighborMuxDead checks this to ignore late events from replaced sessions.
    private int _nextSessionVersion;

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
    public NodeReachableEventArgs? GetReachable(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_stateLock)
        {
            return _reachabilityInfo.TryGetValue(nodeId, out var info) ? info : null;
        }
    }

    /// <inheritdoc />
    public Task<NodeReachableEventArgs> WaitForReachableAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        TaskCompletionSource<NodeReachableEventArgs> tcs;
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return Task.FromException<NodeReachableEventArgs>(
                    new ObjectDisposedException(nameof(MeshMultiplexer)));
            }
            if (_reachabilityInfo.TryGetValue(nodeId, out var info))
            {
                return Task.FromResult(info);
            }
            tcs = new TaskCompletionSource<NodeReachableEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_reachableWaiters.TryGetValue(nodeId, out var list))
            {
                list = new List<TaskCompletionSource<NodeReachableEventArgs>>();
                _reachableWaiters[nodeId] = list;
            }
            list.Add(tcs);
        }

        if (!ct.CanBeCanceled)
        {
            return tcs.Task;
        }

        // Bind cancellation: when the token fires, cancel the TCS so the awaiter
        // observes OperationCanceledException. Leaving the entry in `_reachableWaiters`
        // is fine — a stale TCS that has already transitioned to canceled will be
        // silently ignored when the recompute later signals it.
        var registration = ct.Register(static state =>
        {
            var t = (TaskCompletionSource<NodeReachableEventArgs>)state!;
            t.TrySetCanceled();
        }, tcs);
        return tcs.Task.ContinueWith(static (t, state) =>
        {
            ((CancellationTokenRegistration)state!).Dispose();
            return t.GetAwaiter().GetResult();
        }, registration, CancellationToken.None,
           TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

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

        // Recompute debounce timer. Always created so MarkDirtyAndScheduleRecompute
        // can schedule. With RecomputeDebounce=Zero the timer fires once immediately and
        // the loop completes synchronously.
        _recomputeTimer = new Timer(OnRecomputeTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Optional anti-entropy. Disabled by default (Zero).
        if (_options.TopologyAntiEntropyInterval > TimeSpan.Zero)
        {
            _antiEntropyTimer = new Timer(OnAntiEntropyTimer, null,
                _options.TopologyAntiEntropyInterval, _options.TopologyAntiEntropyInterval);
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
            // Re-check disposed state INSIDE the lock to close the race with a
            // concurrent DisposeAsync that may have flipped _isDisposed after our
            // top-level guard but before we insert.
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(MeshMultiplexer));
            }

            if (_neighbors.ContainsKey(remoteNodeId))
            {
                throw new InvalidOperationException(
                    $"Neighbor '{remoteNodeId}' is already registered. Remove it first.");
            }

            int sessionVersion = ++_nextSessionVersion;
            session = new NeighborSession(this, remoteNodeId, mux, remotePoolId, sessionVersion);
            _neighbors[remoteNodeId] = session;

            // Only advertise this neighbor while it is healthy. If the mux is not yet
            // connected at AddNeighbor time, we wait for its first Connected event
            // before bumping our local version (handled by OnNeighborHealthChanged).
            var selfEntry = GetOrCreateSelfEntry();
            if (session.IsHealthy && !selfEntry.Neighbors.Contains(remoteNodeId))
            {
                selfEntry.Neighbors.Add(remoteNodeId);
                _localVersion++;
                _map.Apply(_options.NodeId, _localVersion, _options.PoolId, selfEntry.Neighbors.ToArray());
            }

            // Seed neighbor with at least a stub entry so it can be picked up by BFS once
            // bidirectional confirmation lands via topology exchange.
            if (!_map.TryGet(remoteNodeId, out _))
            {
                _map.Apply(remoteNodeId, 0, remotePoolId, Array.Empty<string>());
            }

            RecomputeRoutesUnderLock();
        }

        session.Start(_cts.Token);
        RaiseTopologyChanged();
    }

    /// <inheritdoc />
    public void AddNeighbor(string remoteNodeId, MultiplexerOptions muxOptions, string? remotePoolId = null)
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
        ArgumentNullException.ThrowIfNull(muxOptions);

        // Mesh creates and owns the mux — same pattern transports use:
        // MultiplexerOptions carries a StreamFactory that creates the transport.
        var mux = StreamMultiplexer.Create(muxOptions);
        try
        {
            mux.Start();
            AddNeighbor(remoteNodeId, mux, remotePoolId);
            // Mark the session as mesh-owned so the mux gets disposed on remove/dispose.
            lock (_stateLock)
            {
                if (_neighbors.TryGetValue(remoteNodeId, out var session))
                {
                    session.SetOwnsMux();
                }
            }
        }
        catch
        {
            // If AddNeighbor fails after mux creation, clean up the mux.
            try { mux.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            throw;
        }
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

        // Tracked via _pendingNeighborDisposals so DisposeAsync can drain it.
        _ = TrackNeighborDisposal(session!);
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

        // Close the race with a concurrent DisposeAsync that may have snapshotted
        // _openers before our TryAdd.
        if (_isDisposed)
        {
            _openers.TryRemove(key, out _);
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
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
        return opener.SubMultiplexer;
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

        // Close the race with a concurrent DisposeAsync that may have snapshotted
        // _acceptors before our GetOrAdd. If we lost that race, remove ourselves and throw.
        if (_isDisposed)
        {
            _acceptors.TryRemove(key, out _);
            throw new ObjectDisposedException(nameof(MeshMultiplexer));
        }

        if (acceptor.SubMultiplexer is null)
        {
            acceptor.Construct();
        }
        return acceptor.SubMultiplexer ?? throw new InvalidOperationException("Acceptor sub-mux not constructed.");
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
        await GoAwayCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal shutdown that does NOT honour <see cref="_isDisposed"/>. Called from
    /// both <see cref="GoAwayAsync"/> (with the public guard) and <see cref="DisposeAsync"/>
    /// (which has already flipped <c>_isDisposed</c> by the time it runs).
    /// </summary>
    private async Task GoAwayCoreAsync(CancellationToken ct)
    {
        if (_isShuttingDown)
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

        // Stop the debounce + anti-entropy timers before tearing down state so
        // late firings don't touch a half-disposed mesh.
        try { _recomputeTimer?.Dispose(); } catch { }
        try { _antiEntropyTimer?.Dispose(); } catch { }

        try
        {
            // Use the core directly: GoAwayAsync's public guard would short-circuit
            // because we've already set _isDisposed = true above. The core path is
            // what actually disposes NeighborSessions and unsubscribes from neighbor
            // mux lifecycle events.
            await GoAwayCoreAsync(CancellationToken.None).ConfigureAwait(false);
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

        // Wake any opener still waiting on the route signal so they observe shutdown
        // promptly instead of blocking until their timeout.
        TaskCompletionSource routeChange;
        lock (_stateLock)
        {
            routeChange = _routeChange;
        }
        routeChange.TrySetCanceled();

        // Cancel any pending reachability waiters so awaiters of WaitForReachableAsync
        // observe shutdown immediately rather than blocking until their token fires.
        List<TaskCompletionSource<NodeReachableEventArgs>>? waitersToCancel = null;
        lock (_stateLock)
        {
            if (_reachableWaiters.Count > 0)
            {
                waitersToCancel = new List<TaskCompletionSource<NodeReachableEventArgs>>();
                foreach (var list in _reachableWaiters.Values)
                {
                    waitersToCancel.AddRange(list);
                }
                _reachableWaiters.Clear();
            }
        }
        if (waitersToCancel is not null)
        {
            foreach (var tcs in waitersToCancel) tcs.TrySetCanceled();
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

        // Drain any in-flight neighbor disposals from RemoveNeighbor /
        // HandleNeighborMuxDead so they don't leak background work past dispose return.
        var pending = _pendingNeighborDisposals.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }

        // Drain in-flight session closes triggered by inner-mux terminal Disconnected
        // events. These run async on the threadpool; without explicit draining they
        // leak past the dispose return boundary and pollute downstream consumers
        // (subsequent tests, parallel sessions, etc.).
        var pendingCloses = _pendingSessionCloses.Keys.ToArray();
        if (pendingCloses.Length > 0)
        {
            try { await Task.WhenAll(pendingCloses).ConfigureAwait(false); } catch { }
        }

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
        _reachabilityInfo.Clear();
        foreach (var (target, hop) in newRoutes)
        {
            _reachable.Add(target);
            _routes[target] = hop;
            _reachabilityInfo[target] = new NodeReachableEventArgs(
                target, snapshot.PoolOf(target), hop.HopCount);
        }

        // Pulse waiters every recompute. Even if the externally-visible set didn't
        // change, an in-flight opener might be waiting for a specific next-hop to land.
        _routeVersion++;
        var oldChange = _routeChange;
        _routeChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldChange.TrySetResult();

        var toRaiseReachable = new List<NodeReachableEventArgs>();
        var toRaiseUnreachable = new List<string>();

        foreach (var (target, _) in newRoutes)
        {
            if (!prevReachable.Contains(target))
            {
                toRaiseReachable.Add(_reachabilityInfo[target]);
            }
        }
        foreach (string old in prevReachable)
        {
            if (!_reachable.Contains(old))
            {
                toRaiseUnreachable.Add(old);
            }
        }

        // Drain reachability waiters BEFORE leaving the lock so a freshly-reachable
        // node's awaiter resolves with the exact snapshot we just installed. Any
        // node currently in `_reachabilityInfo` (not only first-transitions) wakes
        // all its waiters: the contract is "complete once reachable", which a node
        // that was already reachable trivially satisfies.
        List<(TaskCompletionSource<NodeReachableEventArgs> Tcs, NodeReachableEventArgs Info)>?
            waitersToSignal = null;
        if (_reachableWaiters.Count > 0)
        {
            foreach (var (node, info) in _reachabilityInfo)
            {
                if (_reachableWaiters.TryGetValue(node, out var list))
                {
                    waitersToSignal ??= new();
                    foreach (var tcs in list)
                    {
                        waitersToSignal.Add((tcs, info));
                    }
                    _reachableWaiters.Remove(node);
                }
            }
        }

        if (waitersToSignal is not null)
        {
            // Resolve outside the lock-critical event-raising path so handler
            // continuations don't hold _stateLock.
            foreach (var (tcs, info) in waitersToSignal)
            {
                tcs.TrySetResult(info);
            }
        }

        // Defer event raising to outside the lock via Task.Run-style enqueue.
        if (toRaiseReachable.Count > 0 || toRaiseUnreachable.Count > 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (self, reachable, unreachable) = state;
                foreach (var info in reachable)
                {
                    self.RaiseEvent(self.NodeReachable, info);
                }
                foreach (var node in unreachable)
                {
                    self.RaiseEvent(self.NodeUnreachable, new NodeUnreachableEventArgs(node));
                }
            }, (this, toRaiseReachable, toRaiseUnreachable), preferLocal: false);
        }
    }

    /// <summary>
    /// Wait until the route table changes or <paramref name="ct"/> fires. Returns the
    /// route version observed BEFORE the wait so callers can detect missed updates.
    /// </summary>
    internal Task WaitForRouteChangeAsync(long versionSeen, CancellationToken ct)
    {
        Task task;
        lock (_stateLock)
        {
            if (_routeVersion != versionSeen)
            {
                return Task.CompletedTask;
            }
            task = _routeChange.Task;
        }
        return task.WaitAsync(ct);
    }

    internal long CurrentRouteVersion
    {
        get { lock (_stateLock) { return _routeVersion; } }
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

    /// <summary>
    /// Register an in-flight neighbor disposal so <see cref="DisposeAsync"/> can drain
    /// it. Without tracking, fast add/remove churn leaks background work past dispose
    /// return and pollutes downstream tests.
    /// </summary>
    private Task TrackNeighborDisposal(NeighborSession session)
    {
        var task = session.DisposeAsync().AsTask();
        if (task.IsCompleted)
        {
            return task;
        }
        _pendingNeighborDisposals.TryAdd(task, 0);
        task.ContinueWith(static (t, state) =>
        {
            var dict = (ConcurrentDictionary<Task, byte>)state!;
            dict.TryRemove(t, out _);
        }, _pendingNeighborDisposals, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    internal MeshMultiplexerOptions Options => _options;

    internal CancellationToken ShutdownToken => _cts.Token;

    internal void OnTopologyMessageReceived(IReadOnlyCollection<TopologyEntry> entries)
    {
        _stats.IncrementTopologyReceived();

        bool changed = false;
        bool synchronousRecompute = _options.RecomputeDebounce <= TimeSpan.Zero;
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

            // Keep Apply+Recompute atomic under the lock when debounce is disabled.
            // When debounce is enabled the recompute runs off-thread under its own
            // lock acquisition.
            if (changed && synchronousRecompute)
            {
                RecomputeRoutesUnderLock();
            }
        }

        if (changed)
        {
            if (synchronousRecompute)
            {
                BroadcastLocalTopology();
                RaiseTopologyChanged();
            }
            else
            {
                ScheduleRecompute();
            }
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

    /// <summary>
    /// Schedule a debounced recompute. If a recompute is already pending, this is a
    /// no-op. With <see cref="MeshMultiplexerOptions.RecomputeDebounce"/> = Zero the
    /// timer fires immediately.
    /// </summary>
    private void ScheduleRecompute()
    {
        if (_isDisposed) return;
        if (Interlocked.Exchange(ref _recomputePending, 1) != 0)
        {
            return;
        }
        try
        {
            _recomputeTimer?.Change(_options.RecomputeDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnRecomputeTimer(object? _)
    {
        if (_isDisposed) return;
        if (Interlocked.Exchange(ref _recomputePending, 0) == 0) return;

        lock (_stateLock)
        {
            // _map may have been mutated multiple times since we last ran BFS — drain by
            // running once.
            RecomputeRoutesUnderLock();
        }

        BroadcastLocalTopology();
        RaiseTopologyChanged();
    }

    private void OnAntiEntropyTimer(object? _)
    {
        if (_isDisposed) return;
        // Anti-entropy: re-broadcast local snapshot. Single-flight in NeighborSession
        // ensures this never enqueues a duplicate write.
        try
        {
            BroadcastLocalTopology();
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    /// <summary>
    /// Called by a <see cref="NeighborSession"/> when its underlying mux raises Connected
    /// or Disconnected. The neighbor stays registered in <see cref="_neighbors"/> across
    /// health flips — only the advertised adjacency list excludes unhealthy neighbors so
    /// BFS routes around them, and the neighbor returns to routable state automatically
    /// when its underlying mux recovers. Explicit removal remains a user-only action via
    /// <see cref="RemoveNeighbor"/>.
    /// The <paramref name="sessionVersion"/> check rejects late events from a session that
    /// has already been replaced by a fresh <c>AddNeighbor</c> for the same node ID.
    /// </summary>
    internal void OnNeighborHealthChanged(string remoteNodeId, int sessionVersion, bool healthy)
    {
        if (_isDisposed || _isShuttingDown) return;

        bool changed = false;
        lock (_stateLock)
        {
            if (!_neighbors.TryGetValue(remoteNodeId, out var session))
            {
                return;
            }
            if (session.Version != sessionVersion)
            {
                return;
            }
            if (session.IsHealthy == healthy)
            {
                return;
            }
            session.SetHealthy(healthy);

            var selfEntry = GetOrCreateSelfEntry();
            bool advertisedPresence = selfEntry.Neighbors.Contains(remoteNodeId);
            if (healthy && !advertisedPresence)
            {
                selfEntry.Neighbors.Add(remoteNodeId);
                _localVersion++;
                _map.Apply(_options.NodeId, _localVersion, _options.PoolId, selfEntry.Neighbors.ToArray());
                changed = true;
            }
            else if (!healthy && advertisedPresence)
            {
                selfEntry.Neighbors.Remove(remoteNodeId);
                _localVersion++;
                _map.Apply(_options.NodeId, _localVersion, _options.PoolId, selfEntry.Neighbors.ToArray());
                changed = true;
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

    internal void OnSubMultiplexerOpened()
    {
        _stats.IncrementSubMultiplexers();
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

    /// <summary>
    /// Register an in-flight routed-session close task so <see cref="DisposeAsync"/>
    /// can drain it. The continuation removes the task once it completes so the
    /// collection stays bounded to currently-running closes.
    /// </summary>
    internal void TrackSessionClose(Task closeTask)
    {
        if (closeTask.IsCompleted) return;
        _pendingSessionCloses.TryAdd(closeTask, 0);
        _ = closeTask.ContinueWith(static (t, state) =>
        {
            var dict = (ConcurrentDictionary<Task, byte>)state!;
            dict.TryRemove(t, out _);
        }, _pendingSessionCloses, CancellationToken.None,
           TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
