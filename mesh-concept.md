# Mesh Routing Concept

`MeshMultiplexer` is a proposed multi-hop routing layer for NetConduit, packaged as `NetConduit.Mesh`. It takes direct `IStreamMultiplexer` connections to neighbor nodes and lets you open a full `IStreamMultiplexer` to any reachable node by ID, routing through intermediate nodes invisibly.

Composed entirely on top of the core `NetConduit` package. No changes to core. No transit or transport dependencies.

---

## What it does

- Takes a set of direct `IStreamMultiplexer` connections to neighbor nodes
- Lets you open an `IStreamMultiplexer` to any reachable node by ID
- Routes through intermediate nodes invisibly
- Replicates topology between neighbors via a state-based CRDT
- Re-routes through alternate paths when a relay fails

The mesh is a routing primitive. Discovery (who to dial), authentication, and authorization are the caller's responsibility — the mesh assumes the caller already has `IStreamMultiplexer` connections to neighbors and knows their node IDs.

---

## Partial mesh

`NetConduit.Mesh` is a *partial* mesh — it shares the neighbor muxes with whatever else the caller wants to do on them. The mesh reserves only the `_mesh:` channel prefix. All other channels on the same neighbor mux remain available for direct application traffic.

```csharp
mesh.AddNeighbor("B", muxToB);

// Mesh uses _mesh:* channels on muxToB for routing.
// You can still open your own channels on muxToB at the same time:
var direct = muxToB.OpenChannel(new ChannelOptions { ChannelId = "my-app-data" });
```

---

## Package layout

```
src/NetConduit.Mesh/
    MeshMultiplexer.cs
    MeshMultiplexerOptions.cs
    MeshStats.cs
    Interfaces/
        IMeshMultiplexer.cs
    Internal/
        AdjacencyMap.cs
        BfsRouter.cs
        RouteForwarder.cs
        TopologyReplicator.cs
    Events/
        NodeReachableEventArgs.cs
        NodeUnreachableEventArgs.cs
        TopologyChangedEventArgs.cs
    NetConduit.Mesh.csproj
```

Single dependency: `NetConduit`. The mesh uses the core channel API directly:

- **Routed byte pipes:** `OpenChannel` / `AcceptChannel` → `AsStream()` → `StreamPair` → sub-mux `StreamFactory`
- **Topology sync:** `OpenChannel` / `AcceptChannel` → internal 4-byte length-prefixed JSON
- **Relay forwarding:** `ReadAsync` / `WriteAsync` directly on `IReadChannel` / `IWriteChannel` — no Stream wrapping overhead

---

## Public surface

### IMeshMultiplexer

```csharp
namespace NetConduit.Mesh;

public interface IMeshMultiplexer : IAsyncDisposable
{
    string NodeId { get; }
    string? PoolId { get; }

    bool IsReady { get; }
    bool IsRunning { get; }
    int ReachableNodeCount { get; }
    int KnownNodeCount { get; }
    MeshStats Stats { get; }

    event EventHandler? Ready;
    event EventHandler<NodeReachableEventArgs>? NodeReachable;
    event EventHandler<NodeUnreachableEventArgs>? NodeUnreachable;
    event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
    event EventHandler<Events.ErrorEventArgs>? Error;

    void AddNeighbor(string remoteNodeId, IStreamMultiplexer mux, string? remotePoolId = null);
    void RemoveNeighbor(string remoteNodeId);

    IStreamMultiplexer OpenMultiplexer(string targetNodeId, string multiplexerId);
    Task<IStreamMultiplexer> OpenMultiplexerAsync(
        string targetNodeId, string multiplexerId, CancellationToken ct = default);

    IStreamMultiplexer AcceptMultiplexer(string sourceNodeId, string multiplexerId);
    Task<IStreamMultiplexer> AcceptMultiplexerAsync(
        string sourceNodeId, string multiplexerId, CancellationToken ct = default);

    IAsyncEnumerable<RoutedMultiplexer> AcceptMultiplexersAsync(CancellationToken ct = default);

    ValueTask GoAwayAsync(CancellationToken ct = default);
}
```

`AddNeighbor` registers a neighbor mux for mesh use. The mesh opens only `_mesh:`-prefixed channels on it; the neighbor mux's lifecycle stays with the caller. `RemoveNeighbor` closes any mesh channels the mesh opened on that mux but does not dispose the mux itself.

The events `NodeReachable` / `NodeUnreachable` fire when topology changes make a remote node reachable or unreachable via the mesh — distinct from the transport-level `Connected` / `Disconnected` events on the underlying neighbor muxes.

### MeshMultiplexer

```csharp
public sealed class MeshMultiplexer : IMeshMultiplexer
{
    public static MeshMultiplexer Create(MeshMultiplexerOptions options);
    public void Start();
    public Task WaitForReadyAsync(CancellationToken ct = default);
    // ... all IMeshMultiplexer members
}
```

Lifecycle mirrors `StreamMultiplexer`: `Create` → `Start` → `WaitForReadyAsync` → `GoAwayAsync` → `DisposeAsync`.

### MeshMultiplexerOptions

```csharp
public sealed class MeshMultiplexerOptions
{
    public required string NodeId { get; init; }
    public string? PoolId { get; init; }

    public int MaxHops { get; init; } = 10;
    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRouteRetries { get; init; } = 3;
    public int MaxConcurrentRelays { get; init; } = 100;

    public int DefaultSlabSize { get; init; } = 1_048_576;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxMissedPings { get; init; } = 3;
    public TimeSpan GoAwayTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
}
```

For each routed sub-multiplexer the mesh internally constructs a `MultiplexerOptions` with:

- `StreamFactory` — mesh-managed delegate that establishes and re-establishes routes
- `SessionId` — deterministic from `(sourceNodeId, targetNodeId, multiplexerId)`
- `MaxAutoReconnectAttempts` = `MaxRouteRetries`
- `ConnectionTimeout` = `RouteTimeout`
- Remaining properties carried over from the options above

### Events

```csharp
public sealed class NodeReachableEventArgs(string NodeId, string? PoolId, int HopCount) : EventArgs;
public sealed class NodeUnreachableEventArgs(string NodeId) : EventArgs;
public sealed class TopologyChangedEventArgs(int KnownNodeCount, int ReachableNodeCount) : EventArgs;
```

### RoutedMultiplexer

```csharp
public readonly record struct RoutedMultiplexer(
    string SourceNodeId,
    string MultiplexerId,
    IStreamMultiplexer Multiplexer);
```

### MeshStats

```csharp
public sealed class MeshStats
{
    public int ActiveSubMultiplexers { get; }
    public int ActiveRelays { get; }
    public long RoutesOpened { get; }
    public long RoutesFailed { get; }
    public long RelayBytesForwarded { get; }
    public long TopologyMessagesSent { get; }
    public long TopologyMessagesReceived { get; }
}
```

---

## Usage

```csharp
await using var mesh = MeshMultiplexer.Create(new MeshMultiplexerOptions
{
    NodeId = "A",
    PoolId = "us"
});
mesh.Start();

mesh.AddNeighbor("B", muxToB, remotePoolId: "us");
mesh.AddNeighbor("C", muxToC, remotePoolId: "eu");

await mesh.WaitForReadyAsync();

await using var subMux = await mesh.OpenMultiplexerAsync("D", "rpc-1");

var writer = subMux.OpenChannel(new ChannelOptions { ChannelId = "requests" });
await writer.WaitForReadyAsync();
await writer.WriteAsync(payload);
```

The returned `IStreamMultiplexer` is a real `StreamMultiplexer`. Same channel API, same backpressure, same statistics, same transit compatibility. The routing is invisible to the caller.

On the acceptor side:

```csharp
await foreach (var routed in mesh.AcceptMultiplexersAsync(ct))
{
    var (sourceNodeId, multiplexerId, subMux) = routed;
    // Use subMux exactly like any IStreamMultiplexer
    _ = HandleAsync(sourceNodeId, subMux, ct);
}
```

---

## How it works

### Sub-multiplexer construction

`StreamMultiplexer.Create` needs a `StreamFactoryDelegate` returning `Task<IStreamPair>`. The core API provides everything needed:

- `IWriteChannel.AsStream()` → write-only `Stream`
- `IReadChannel.AsStream()` → read-only `Stream`
- `new StreamPair(readStream, writeStream)` → `IStreamPair`

For `mesh.OpenMultiplexerAsync("D", "rpc-1")`:

1. BFS on the adjacency map → next hop = B, path = `[B, C, D]`
2. `channelBase = "_mesh:route:D:A/rpc-1:0"` (trailing `:0` is a monotonic nonce that prevents collision when the same `(source, target, multiplexerId)` tuple is reused after a close)
3. Build `MultiplexerOptions`:
   ```csharp
   StreamFactory = async ct =>
   {
       var writer = nextHopMux.OpenChannel(
           new ChannelOptions { ChannelId = channelBase + ">>" });
       var reader = nextHopMux.AcceptChannel(channelBase + "<<");
       await Task.WhenAll(
           writer.WaitForReadyAsync(ct),
           reader.WaitForReadyAsync(ct));
       return new StreamPair(reader.AsStream(), writer.AsStream());
   };
   ```
4. `StreamMultiplexer.Create(options)`, `Start()`, return as `IStreamMultiplexer`

### Relay forwarding

At an intermediate node B, when `AcceptChannelsAsync` on `B↔A` mux yields `_mesh:route:D:A/rpc-1:0>>`:

1. Parse prefix → target = D, source = A. Target != self → relay.
2. BFS for next hop toward D → C
3. On `B↔A` mux: accept the inbound read channel (`>>`), open the response write channel (`<<`)
4. On `B↔C` mux: open the forward write channel (`>>`), accept the response read channel (`<<`)
5. Splice both directions with raw `ReadAsync` / `WriteAsync`:
   ```csharp
   await Task.WhenAll(
       PipeAsync(incomingReader, forwardWriter, ct),   // A → B → C
       PipeAsync(responseReader, responseWriter, ct)); // C → B → A
   ```

The relay uses `IReadChannel` / `IWriteChannel` directly — no Stream wrapping, no allocation overhead per hop. The relay never parses NetConduit framing inside the routed pipe; it sees opaque bytes. The sub-mux protocol runs end-to-end between A and D.

**Relay resource limits:** Each relay tracks concurrent relay count. When `MaxConcurrentRelays` is reached, new relay requests are rejected by closing the inbound channel with an error. The opener's sub-mux will retry through `StreamFactory`, finding an alternate path if one exists.

### Terminus

At node D, when `AcceptChannelsAsync` on `D↔C` mux yields `_mesh:route:D:A/rpc-1:0>>`:

1. Parse prefix → target = D = self. This is the terminus.
2. Accept the read channel (`>>`), open the response write channel (`<<`)
3. Construct a `StreamMultiplexer` over the channel pair:
   ```csharp
   StreamFactory = async ct =>
   {
       // First call: use the channels that just arrived.
       // Reconnect calls: await the next incoming route for (A, "rpc-1").
       var (reader, writer) = await AwaitRouteChannelsAsync("A", "rpc-1", ct);
       return new StreamPair(reader.AsStream(), writer.AsStream());
   };
   SessionId = deterministic(A, D, "rpc-1"); // same as A's side
   MaxAutoReconnectAttempts = MaxRouteRetries;
   ```
4. Emit the `RoutedMultiplexer` via `AcceptMultiplexersAsync`

### Topology replication

Each direct neighbor gets a channel pair for topology exchange: `_mesh:topo>>` (write) and `_mesh:topo<<` (read).

**Wire format:** 4-byte big-endian length prefix + UTF-8 JSON payload. The adjacency map is small (node count × ~100 bytes), so full-state exchange is used — no delta protocol required.

**State:**

```csharp
internal sealed record AdjacencyMap(Dictionary<string, NodeEntry> Nodes);

internal sealed record NodeEntry(
    long Version,
    HashSet<string> Neighbors,
    string? PoolId);
```

**Merge rule:** For each `(nodeId, entry)` in a received map, take the entry with the higher `Version`. Only the owning node may increment its own `Version`. This is a Last-Writer-Wins Register per node — commutative, associative, idempotent.

**Edge validity:** An edge `A↔B` is routable only when `A.Neighbors` contains B *and* `B.Neighbors` contains A. Unilateral claims are ignored. This handles stale entries cleanly: if A crashes, B runs `RemoveNeighbor("A")`, B's entry drops A, the edge becomes one-sided and unroutable. A's orphaned entry is harmless until garbage-collected.

**Garbage collection:** Node entries with zero confirmed bidirectional edges for longer than `2 × PingInterval × MaxMissedPings` are dropped from the local map and the removal propagated.

**Flow on `AddNeighbor("B", muxToB, "us")`:**

1. Update local map: `self.Neighbors += B`, bump `self.Version`
2. Open topology channels on `A↔B` mux:
   - `OpenChannel(_mesh:topo>>)` for outbound
   - `AcceptChannel(_mesh:topo<<)` for inbound
3. Send the full local map as length-prefixed JSON on the write channel
4. Background loop: read length-prefixed messages from the read channel
5. Merge each received entry by `Version`. If the local map changed, send the updated full map to **all other** neighbor topology channels
6. Propagation converges when no map changes (usually 1–2 hops on small topologies)

**Flow on `RemoveNeighbor("B")`:**

1. `self.Neighbors -= B`, bump `self.Version`
2. Close the topology channels for B
3. Send the updated map to remaining neighbor topology channels

**On neighbor reconnect:** When a neighbor mux raises `Connected` after a reconnect, the topology channels resume via the mux's built-in replay. Both sides re-send their full local map to ensure consistency.

### Routing

`BfsRouter` operates on the local `AdjacencyMap`, considering only edges that are:

1. Bidirectionally confirmed (both endpoints' `Neighbors` agree)
2. Locally observable as `IsConnected` (transient drops are skipped locally, not broadcast)

Rules:

- Shortest path wins (BFS hop count)
- Tie-break: among equal-length paths, prefer paths whose intermediate nodes share `PoolId` with the source. Pool affinity **never adds hops** — it only orders ties.
- Route results are cached and invalidated on `TopologyChanged`
- No route found → buffer the pending sub-mux until topology converges or `RouteTimeout` elapses
- Hop count > `MaxHops` → throw `MeshRoutingException`

### Reroute on failure

When a relay node dies, the routed channel pair breaks. Both endpoints' sub-muxes detect the disconnect.

**Opener side:** `MaxAutoReconnectAttempts > 0`, so the sub-mux calls its `StreamFactory` again. The mesh-managed factory runs BFS against the (possibly updated) topology, opens a new channel pair through alternate relays, and returns the new `StreamPair`. The sub-mux performs its reconnect handshake (the deterministic `SessionId` matches), replays unacknowledged frames, and resumes.

**Acceptor side:** Same mechanism. The acceptor's sub-mux calls its `StreamFactory`, which blocks until the mesh receives a new incoming route for the same `(source, multiplexerId)` pair. When it arrives, the factory resolves and the sub-mux performs its reconnect handshake.

**Deterministic SessionId** ensures both sides agree:

```
SessionId = Guid(SHA256(sorted(sourceNodeId, targetNodeId) || multiplexerId)[..16])
```

Both sides compute the same value independently. It survives path changes — only the byte pipe is different, the sub-mux session is conceptually continuous.

**Outcomes:**

- Alternate path found → seamless reroute, application sees only transient `Disconnected` / `Connected` on the sub-mux
- No alternate path and `RouteTimeout` elapses → `StreamFactory` faults. After `MaxRouteRetries` failures, sub-mux disposes. Application sees a final `Disconnected`.
- `MaxRouteRetries = 0` → sub-mux dies immediately on first failure

### Pending opens

`StreamMultiplexer` already supports opening before the transport is ready — channels sit in `Opening` state and writes buffer in the channel slab. The mesh reuses this:

- `OpenMultiplexer` returns synchronously. The underlying `StreamFactory` has not yet resolved.
- The user opens channels and writes data. Everything buffers in the sub-mux's slab.
- Once the route lands and both ends handshake, channels transition to `Open` and buffered writes flush.

No new pending-state machinery is needed.

---

## Channel naming

The mesh reserves the `_mesh:` channel prefix on every registered neighbor mux.

| Channel ID | Direction | Purpose |
|---|---|---|
| `_mesh:topo>>` | Write to neighbor | Topology state (length-prefixed JSON) |
| `_mesh:topo<<` | Read from neighbor | Topology state (length-prefixed JSON) |
| `_mesh:route:{target}:{source}/{id}:{nonce}>>` | Write toward target | Routed byte pipe (outbound leg) |
| `_mesh:route:{target}:{source}/{id}:{nonce}<<` | Read from target | Routed byte pipe (inbound leg) |

The `>>` / `<<` suffixes follow the NetConduit duplex-channel-pair convention.

Caller must not open channels with the `_mesh:` prefix on any mux registered as a neighbor. The mesh inspects channels arriving via `AcceptChannelsAsync` and routes only those matching the reserved prefix.

---

## Lifecycle

### Start

`MeshMultiplexer.Start()` begins:

- Running an `AcceptChannelsAsync` loop on each registered neighbor mux to handle inbound mesh channels
- Hosting topology channels for each neighbor

`WaitForReadyAsync` returns once at least one neighbor's topology channel pair is ready and the first topology exchange has completed.

### GoAway

`MeshMultiplexer.GoAwayAsync()`:

- Calls `GoAwayAsync()` on every active routed sub-mux
- Closes every `_mesh:`-prefixed channel the mesh opened on neighbor muxes
- Does **not** call `GoAwayAsync` or dispose any neighbor mux — the caller owns those
- After return, neighbor muxes are fully usable for non-mesh traffic

### Dispose

`DisposeAsync` is `GoAwayAsync` followed by hard cancellation of any remaining work. Neighbor muxes are still not touched.

---

## What stays unchanged in core

| Component | Status |
|---|---|
| `StreamMultiplexer` | Unchanged |
| `IStreamMultiplexer` | Unchanged |
| Framing protocol | Unchanged |
| All transports | Unchanged |
| All existing transits | Unchanged |

The mesh is pure composition. `NetConduit.Mesh` uses only the core channel API. No new framing, no new wire protocol.

---

## What is out of scope

The mesh is a routing primitive. The following belong to the caller:

- Peer discovery — who to dial, how to find addresses
- Authentication — who is allowed in the mesh
- Authorization — which nodes may route through which
- Reconnection policy at the neighbor link level — when to drop or re-add a direct neighbor mux
- Custom gossip beyond topology — presence, metadata, application-level state

The mesh assumes the caller already has `IStreamMultiplexer` connections to neighbors and knows their node IDs.

---

## Docs layout

```
docs/
    concepts/
        mesh.md              — overview, routing, topology CRDT, pool affinity
    api/
        mesh-multiplexer.md  — MeshMultiplexer / MeshMultiplexerOptions reference
    samples/
        mesh-3-node.md       — A↔B↔C, open mux from A to C through B
```

`docs/packages.md` gains an entry for `NetConduit.Mesh`. `docs/concepts/index.md` links to the mesh concept page.

---

## Tests

Two assemblies under `tests/`:

- `NetConduit.Mesh.UnitTests/` — pure unit tests for the internal components (adjacency map, BFS router, channel naming, topology wire format). Fast, no I/O, no real muxes. Property-based where applicable.
- `NetConduit.Mesh.IntegrationTests/` — end-to-end tests with real `StreamMultiplexer` instances connected over `IpcMultiplexer` (or in-memory `IStreamPair` fakes for the fastest cases). Cover routing, reroute, partial mesh, lifecycle, concurrency.

Every public method, every code path, every failure mode has tests. No optimism.

### Unit tests

**`AdjacencyMapMergeTests`** — the CRDT layer

- Empty maps merge to empty
- Single-entry map merges into empty
- Higher `Version` wins on conflict for the same node
- Equal `Version` wins last write deterministically by `(Version, NodeId)` ordering
- Lower `Version` is rejected
- Merge of disjoint node sets is union
- Merge is commutative: `merge(a, b) == merge(b, a)` (property test, 1000 random pairs)
- Merge is associative: `merge(merge(a, b), c) == merge(a, merge(b, c))` (property test)
- Merge is idempotent: `merge(a, a) == a` (property test)
- Merging two valid maps never produces a `Version` lower than either input
- Garbage collection drops zero-bidirectional-edge entries past the threshold
- Garbage collection does **not** drop entries that have at least one confirmed bidirectional edge
- Garbage collection clock is monotonic — no premature drops under clock skew (simulated `IClock`)
- `Neighbors` set merge for the same node at higher `Version` replaces wholesale (not union — owning node is authoritative for its own neighbor list)
- `PoolId` change with bumped `Version` propagates
- `PoolId` change without `Version` bump is rejected
- Removing a neighbor bumps `Version` and removes from `Neighbors`
- Re-adding a removed neighbor produces a later-`Version` entry that wins over the removal
- Maliciously-crafted incoming entry that claims `Version = long.MaxValue` for a node other than itself: the owning node's local entry is unaffected (only the owning node may bump its own version)
- Map serialization round-trip via the length-prefixed JSON wire format preserves equality
- Map deserialization rejects malformed JSON
- Map deserialization rejects length-prefix overflow attempts

**`BfsRouterTests`** — the path-finding layer

- Single-hop neighbor returns one-hop path
- Multi-hop path follows BFS shortest distance
- Disconnected graph returns no route
- Self-loop returns empty path
- Asymmetric edges (A claims B, B does not claim A) are excluded
- One-sided edges from crashed nodes are excluded
- Locally-disconnected neighbor mux is excluded even if topology shows the edge
- Pool affinity tie-breaks among equal-length paths
- Pool affinity **never** lengthens a path: a hops-3 same-pool path loses to a hops-2 cross-pool path
- Pool affinity with all candidates in the same pool returns shortest path
- Pool affinity with no same-pool candidates returns shortest path
- Hop count > `MaxHops` returns no route
- Hop count exactly equal to `MaxHops` returns route
- Cache returns same result for same query
- Cache is invalidated on `TopologyChanged`
- Cache invalidation under concurrent reads doesn't return stale routes
- Two equal-length, equal-pool paths produce a deterministic choice (stable across calls until topology change)
- Cycle in topology is handled without infinite loop
- 1000-node random topology returns correct shortest path (compared against Dijkstra reference)
- Removing a node mid-search invalidates and re-computes

**`ChannelNamingTests`** — the naming protocol

- Route channel name round-trips: build → parse → equal
- Parse rejects names without `_mesh:` prefix
- Parse rejects malformed segments
- Parse rejects unknown subprefixes (`_mesh:unknown:...`)
- Topology channel suffixes (`>>`, `<<`) are recognized
- Nonce monotonicity within a single mesh instance is enforced
- Two different mesh instances at the same node never produce colliding nonces in the same wall-clock second (uses `(timestamp, counter)` not just counter)
- Node IDs containing `:` are rejected at `MeshMultiplexerOptions` validation
- Node IDs containing `/` are rejected at `MeshMultiplexerOptions` validation
- Node IDs containing `>>` or `<<` are rejected at `MeshMultiplexerOptions` validation
- Node IDs of zero length are rejected
- Multiplexer IDs containing reserved characters are rejected at `OpenMultiplexerAsync`
- Source / target / multiplexer ID with maximum allowed length round-trips correctly
- ASCII control characters in node IDs are rejected
- Empty multiplexer ID is rejected
- Path-style multiplexer ID (`rpc/v1`) round-trips correctly

**`TopologyWireFormatTests`** — the on-the-wire JSON

- 4-byte big-endian length prefix is written correctly for small, medium, and large maps
- Length prefix is read correctly for the same range
- Length-prefix value of zero is rejected as malformed
- Length-prefix exceeding `MaxTopologyMessageSize` is rejected and channel closed with error
- Partial read of the length prefix blocks correctly until full
- Partial read of the payload blocks correctly until full
- Channel close mid-prefix surfaces as `EndOfStreamException`
- Channel close mid-payload surfaces as `EndOfStreamException`
- Unicode node IDs (CJK, emoji, RTL) round-trip
- Concurrent writers on the same write channel serialize correctly (no interleaving)
- Writer back-pressure under slow reader does not corrupt frames

**`DeterministicSessionIdTests`**

- Same `(sourceNodeId, targetNodeId, multiplexerId)` produces the same `Guid` on both sides regardless of which side is "source" (sort first)
- Sort order is stable for any input pair
- Different `multiplexerId` produces a different `Guid`
- Different node pair produces a different `Guid`
- 1,000,000 random `(nodeA, nodeB, multiplexerId)` triples produce no collisions within the run
- Unicode node IDs produce stable hashes (UTF-8 canonicalization)
- Hash is stable across .NET versions (frozen test vectors)

### Integration tests

**`PartialMeshTests`** — coexistence with non-mesh traffic

- User-opened channel on a neighbor mux is unaffected by `AddNeighbor`
- User-opened channel data is not seen by the mesh
- User reading from a non-`_mesh:` channel is not blocked by mesh traffic on the same mux
- Mesh routing does not consume bandwidth budget the user reserved on the same mux
- `RemoveNeighbor` does not close user-opened channels
- `MeshMultiplexer.DisposeAsync` does not close user-opened channels
- `MeshMultiplexer.DisposeAsync` does not call `GoAwayAsync` on the neighbor mux
- User opens a channel named `_mesh:something` — mesh either ignores it (if not a recognized subprefix) or rejects it; verify both cases
- User and mesh both have heavy concurrent traffic on the same mux for 30 seconds — neither corrupts the other's data
- Mesh control channels (topology) survive the user opening and closing hundreds of unrelated channels

**`TwoNodeMeshTests`** — the simplest case

- A↔B with `AddNeighbor` on both, `WaitForReadyAsync` returns on both
- `NodeReachable` fires for B at A and for A at B
- Open sub-mux A→B, write bytes, read bytes on B's accept side
- Multiple sub-muxes A→B over the same neighbor link don't interfere
- Bidirectional sub-muxes A→B and B→A coexist
- Sub-mux open before `WaitForReadyAsync` completes buffers writes correctly
- Closing the neighbor mux closes all sub-muxes that depend on it
- Re-adding a previously removed neighbor restores reachability

**`ThreeNodeLineTests`** — A↔B↔C, the relay basics

- C reachable from A after topology converges
- A's `NodeReachable` fires for C with `HopCount = 2`
- Open sub-mux A→C, write 1 KB, verify byte-for-byte at C
- Open sub-mux A→C, write 100 MB streaming, verify total at C (catches buffer overflow / leaks)
- Open sub-mux A→C, write zero bytes then close, C sees EOF cleanly
- Open sub-mux A→C, open multiple channels inside it (RPC, events, file transfer), all work in parallel
- Sub-mux statistics on A and C agree on bytes transferred (within the resync window)
- Backpressure on C propagates through B back to A (writes on A block when C is slow)
- B's relay stats show bytes forwarded equal to (A→C bytes) + (C→A bytes)
- B sees `_mesh:route:*` channels at both `A↔B` and `B↔C` ends with matching nonces
- Killing C cleanly drains in-flight bytes at B and surfaces `Disconnected` at A
- A→C with `MaxHops = 1` faults with `MeshRoutingException`

**`DiamondTopologyTests`** — A↔B, A↔C, B↔D, C↔D (alternate paths exist)

- BFS picks one path deterministically among equal-length options
- Pool affinity picks the same-pool path when configured
- Open sub-mux A→D, kill the chosen relay (B), sub-mux reroutes through C, no data loss
- Open sub-mux A→D streaming bytes, kill B mid-stream, verify total bytes at D match total bytes sent at A (replay buffer covers the gap)
- Sub-mux `Reconnecting` event fires during reroute
- Sub-mux `Connected` event fires after reroute completes
- `SessionId` on both sides is identical before and after reroute
- Sub-mux statistics persist across reroute
- Both relays B and C handle traffic simultaneously when two sub-muxes pick different paths

**`StarTopologyTests`** — hub H with 10 spokes

- All spokes reachable from each other through H within `2 × PingInterval`
- 10 sub-muxes opened simultaneously (each spoke to next spoke) all succeed
- H's `ActiveRelays` reaches 10
- One spoke's link to H breaks; that spoke becomes unreachable from all others
- That spoke's link to H restores; reachability returns
- All sub-muxes survive H's link to one of them flapping (the unaffected ones)

**`RelayLimitTests`**

- Configure `MaxConcurrentRelays = 5`. Open 10 sub-muxes that must relay through one node. First 5 succeed, next 5 fault or reroute.
- Rejected relay request closes the inbound channel with the documented error code
- Opener's `StreamFactory` sees the rejection and tries an alternate path
- Opener with no alternate path gets `MeshRoutingException` after `MaxRouteRetries`
- After one relay completes, the slot is reclaimed; the next pending opener succeeds
- Reaching the limit does not affect user (non-mesh) traffic on the same neighbor mux
- `ActiveRelays` in `MeshStats` is accurate at all times during the test
- Two relays competing for the last slot — exactly one wins, the other is rejected (no double-count)

**`TopologyConvergenceTests`**

- Two nodes added simultaneously by two different sources converge to the same map at every node
- A node added then removed within `PingInterval` converges to "removed" at every node
- Concurrent `AddNeighbor` calls on the same mesh (race) produce one consistent map
- A 10-node mesh adds one neighbor at the edge; the new node is reachable at every other node within bounded time
- Stale entry (sender crashed) is GC'd after `2 × PingInterval × MaxMissedPings`
- GC is suspended while the node has at least one confirmed bidirectional edge
- A reconnects to B: B's topology channel resumes, both re-exchange full maps
- A delta arriving out of order (older `Version` after newer `Version`) is rejected without disrupting state
- Topology channel full → backpressure on the topology writer doesn't deadlock the mesh
- Topology channel disconnect followed by reconnect doesn't produce duplicate `NodeReachable` events for nodes that stayed reachable

**`MeshRerouteTests`** — the heart of the design

- Open A→D over [B, C], kill B, verify D-side sub-mux sees `Reconnecting`
- Reroute completes within `RouteTimeout`
- Bytes written on A during the disconnect window are delivered exactly once at D (no loss, no duplication)
- Bytes in-flight at the moment B dies are eventually delivered at D (replay buffer)
- `SessionId` validated on reconnect handshake matches
- Mismatched `SessionId` (manually corrupted) aborts the reconnect with the documented error
- No alternate path available + `MaxRouteRetries = 3` → 3 retry attempts at `RouteTimeout` each, then `Disconnected` event with the documented reason
- `MaxRouteRetries = 0` → immediate `Disconnected` on first relay failure
- Acceptor-side `StreamFactory` blocks until the opener's new route arrives
- Acceptor side cancels its `StreamFactory` wait when `RouteTimeout` elapses
- Reroute across a topology change: B dies, BFS reruns with updated map, alternate path through new node is found
- Concurrent reroutes of multiple sub-muxes through the same dead relay do not deadlock

**`LifecycleTests`**

- `Create` does not start any I/O
- `Start` called twice throws `InvalidOperationException`
- `WaitForReadyAsync` with no neighbors blocks until one is added
- `WaitForReadyAsync` honors `CancellationToken`
- `WaitForReadyAsync` after `DisposeAsync` throws `ObjectDisposedException`
- `GoAwayAsync` calls `GoAwayAsync` on every active sub-mux
- `GoAwayAsync` closes every `_mesh:*` channel the mesh opened
- `GoAwayAsync` does **not** call `GoAwayAsync` on neighbor muxes
- `GoAwayAsync` does **not** call `DisposeAsync` on neighbor muxes
- `GoAwayAsync` returns within `GoAwayTimeout` even if a sub-mux refuses to drain
- `DisposeAsync` after `GoAwayAsync` is idempotent
- `DisposeAsync` without `GoAwayAsync` cleanly cancels in-flight work
- All public methods throw `ObjectDisposedException` after `DisposeAsync`
- Disposing during an in-progress `OpenMultiplexerAsync` cancels the open
- Disposing during an in-progress `AcceptMultiplexerAsync` cancels the accept
- No deadlock when the user calls `DisposeAsync` from inside an event handler
- `Error` event fires for unhandled exceptions on background loops (does not crash the process)
- `Error` event handler that throws does not prevent further error reporting

**`EventTests`**

- `Ready` fires exactly once
- `NodeReachable` fires once per node when it becomes reachable
- `NodeReachable` does not fire again on intermittent topology churn within `IsConnected` (debounced)
- `NodeUnreachable` fires when the last path to a node disappears
- `NodeReachable` fires again when the node returns
- `TopologyChanged` fires on every adjacency-map change
- Events fire in correct order during a complex sequence (add, become reachable, lose path, become unreachable, remove)
- Event handlers run on a thread-pool thread, not on the I/O thread (verified by observing thread IDs and ensuring no I/O deadlock when a handler blocks)
- Removing a subscribed handler stops further deliveries to it
- Handler exceptions are surfaced via `Error` event, not swallowed silently, not crashing

**`OpenAcceptSemanticsTests`**

- `OpenMultiplexer` returns synchronously with a sub-mux in `Opening` state
- Writes on a channel of the pending sub-mux buffer correctly
- Writes flush when the route lands
- `OpenMultiplexerAsync` completes when the sub-mux becomes ready
- `OpenMultiplexerAsync` honors `CancellationToken`
- `OpenMultiplexerAsync` with unknown target node blocks until topology brings it or `RouteTimeout` elapses
- Opening the same `(target, multiplexerId)` twice while the first is alive throws
- Opening the same `(target, multiplexerId)` after the first is closed succeeds with a new nonce
- `AcceptMultiplexerAsync` honors `CancellationToken`
- `AcceptMultiplexerAsync` with no pending route blocks until one arrives
- `AcceptMultiplexersAsync` enumerates concurrent inbound sub-muxes correctly
- `AcceptMultiplexersAsync` honors `CancellationToken` on the foreach
- Acceptor disposing of a yielded sub-mux mid-enumeration does not break the enumerator
- Opener and acceptor see matching `SourceNodeId`, `MultiplexerId` on the `RoutedMultiplexer` value

**`SubMultiplexerCompositionTests`** — does the routed mux behave like a real one

- A `StreamTransit` opened on a routed sub-mux carries bytes correctly (relay is transparent)
- A `MessageTransit<T>` opened on a routed sub-mux carries messages correctly
- A `DuplexStreamTransit` opened on a routed sub-mux is fully bidirectional
- A `DeltaMessageTransit<T>` opened on a routed sub-mux replicates state correctly
- All routed sub-mux events (`Ready`, `Connected`, `Disconnected`) fire at the right times
- Sub-mux statistics on opener and acceptor agree
- Sub-mux backpressure works (slow reader stalls writer)
- Sub-mux `GoAwayAsync` performs graceful shutdown end-to-end through the relay

**`SecurityAndValidationTests`**

- Opening with `targetNodeId` of zero length throws `ArgumentException`
- Opening with `multiplexerId` of zero length throws `ArgumentException`
- Opening with extremely long IDs throws `ArgumentException` (configured limit)
- A neighbor sending a topology entry claiming to be the local node is rejected (self-shadow attempt)
- A neighbor sending a topology entry claiming to be a node it has no edge to is accepted (it's just routing info, but cannot give a shorter false path because edges must be bidirectional)
- A relay receiving a route channel for a target it has no path to closes the channel with the documented error
- A relay receiving a route channel that would create a forwarding loop (target = self in an incoming relay context where target is somewhere else, plus already-relaying) rejects
- Malformed `_mesh:route:*` channel names from a malicious neighbor are rejected without crashing
- Garbage bytes injected into the topology channel cause the channel to close, do not crash the mesh
- Garbage bytes injected into a relay byte pipe surface as sub-mux protocol errors at the terminus (not at the relay)
- Adding the same neighbor ID twice (same or different mux) throws or replaces deterministically (specify which)
- Removing a non-existent neighbor is a no-op (not an exception)

**`MeshStatsTests`**

- `ActiveSubMultiplexers` increments on open, decrements on close (opener and acceptor sides)
- `ActiveRelays` increments on relay start, decrements on relay end
- `RoutesOpened` is monotonic, increments on each successful open including reroutes
- `RoutesFailed` increments on each `MeshRoutingException` outcome
- `RelayBytesForwarded` is monotonic and matches the sum of bytes forwarded across all relays
- `TopologyMessagesSent` / `TopologyMessagesReceived` increment per length-prefixed message exchanged
- Stats are thread-safe under concurrent reads while updates happen
- Stats survive `GoAwayAsync` (final values readable until `DisposeAsync`)

**`ChaosTests`** — long-running stress

- Run a 7-node fully-meshed cluster for 5 minutes with:
  - Random `AddNeighbor` / `RemoveNeighbor` every 100 ms
  - Random `OpenMultiplexerAsync` / sub-mux dispose every 200 ms
  - Per-sub-mux: random `OpenChannel` and bursts of writes
  - One node randomly killed and restarted every 30 s
- Invariants checked continuously: no deadlock, no memory leak (sub-mux count returns to baseline when load stops), no orphaned channels (`_mesh:*` channel count returns to baseline)
- At the end: stop all activity, drain, verify topology converges to ground truth
- At the end: every node's `MeshStats` shows balanced counters (opened == closed, in-flight bytes == 0)
- Re-run with `MaxConcurrentRelays = 2` (forcing rejections) — same invariants hold
- Re-run with random network latency injection (0–500 ms) — same invariants hold

**`PerformanceRegressionTests`** (smoke, not benchmarks)

- 3-node line, 1 sub-mux: > 200 MB/s on loopback (catches catastrophic regressions)
- 3-node line, 100 channels of 1 KB each: > 100k msg/s (catches scheduler regressions)
- 10-node fully-meshed, topology converges in < 1 s
- Open + close of 10,000 sub-muxes in sequence completes in < 30 s with stable memory
