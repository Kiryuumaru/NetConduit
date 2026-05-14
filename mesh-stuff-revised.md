# Mesh Routing for NetConduit

Folding multi-hop routing into NetConduit as a first-class `MeshMultiplexer`, layered entirely on core primitives (`StreamMultiplexer`, `IWriteChannel`, `IReadChannel`, `StreamPair`). No changes to NetConduit core. No transit dependencies.

---

## Overview

`MeshMultiplexer` is a composition layer that:

- Takes a set of direct `IStreamMultiplexer` connections to neighbors
- Lets you open an `IStreamMultiplexer` to **any** reachable node by ID
- Routes through intermediate nodes invisibly
- Replicates topology via a state-based CRDT over raw channels
- Re-routes through alternate paths when a relay fails (sub-mux reconnection handles replay)

Consumers (GridConduit, others) bring discovery, auth, and policy. NetConduit owns the wire and the routing.

---

## Package layout

One new package, mirroring the existing naming pattern:

```
src/NetConduit.Mesh/
    MeshMultiplexer.cs
    MeshMultiplexerOptions.cs
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

`NetConduit.Mesh.csproj` references:

| Package | Why |
|---------|-----|
| `NetConduit` | `IStreamMultiplexer`, `StreamMultiplexer`, `MultiplexerOptions`, `IStreamPair`, `StreamPair`, `IWriteChannel`, `IReadChannel` |

No transit or transport dependencies. The mesh uses the core channel API directly:

- **Routed byte pipes:** `OpenChannel` / `AcceptChannel` → `AsStream()` → `StreamPair` → sub-mux `StreamFactory`
- **Topology sync:** `OpenChannel` / `AcceptChannel` → internal 4-byte length-prefixed JSON protocol
- **Relay forwarding:** `ReadAsync` / `WriteAsync` directly on channels — no Stream wrapping overhead

---

## Public surface

### IMeshMultiplexer

```csharp
namespace NetConduit.Mesh;

/// <summary>
/// Multi-hop routing layer over a set of direct <see cref="IStreamMultiplexer"/> neighbor connections.
/// Opens routed <see cref="IStreamMultiplexer"/> sessions to any reachable node.
/// </summary>
public interface IMeshMultiplexer : IAsyncDisposable
{
    string NodeId { get; }
    string? PoolId { get; }

    bool IsReady { get; }
    bool IsRunning { get; }
    int ReachableNodeCount { get; }
    int KnownNodeCount { get; }

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

Notes:

- `AddNeighbor` / `RemoveNeighbor` registers a neighbor mux for mesh routing. The mesh opens only `_mesh:`-prefixed channels on it. User code keeps full access to all other channels on the same mux.
- `OpenMultiplexer` / `AcceptMultiplexer` return `IStreamMultiplexer` — the interface exists in core (`src/NetConduit/Interfaces/IStreamMultiplexer.cs`).
- `GoAwayAsync` returns `ValueTask` matching `IStreamMultiplexer.GoAwayAsync`.
- Events use `NodeReachable` / `NodeUnreachable` (not Connected/Disconnected) to avoid confusion with the transport-level events on `IStreamMultiplexer`.

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

Follows the same lifecycle as `StreamMultiplexer`: `Create` → `Start` → `WaitForReadyAsync`.

### MeshMultiplexerOptions

```csharp
public sealed class MeshMultiplexerOptions
{
    public required string NodeId { get; init; }
    public string? PoolId { get; init; }

    // Routing
    public int MaxHops { get; init; } = 10;
    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRouteRetries { get; init; } = 3;
    public int MaxConcurrentRelays { get; init; } = 100;

    // Sub-multiplexer defaults (applied to every routed sub-mux)
    public int DefaultSlabSize { get; init; } = 1_048_576;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxMissedPings { get; init; } = 3;
    public TimeSpan GoAwayTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
}
```

The mesh constructs `MultiplexerOptions` for each routed sub-mux internally, setting:

- `StreamFactory` — mesh-managed delegate that establishes/re-establishes routes
- `SessionId` — deterministic from `(sourceNodeId, targetNodeId, multiplexerId)`
- `MaxAutoReconnectAttempts` — set to `MaxRouteRetries` (enables reroute on failure)
- `ConnectionTimeout` — set to `RouteTimeout`
- All other properties from the options above

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

---

## Usage

```csharp
// Node A
await using var mesh = MeshMultiplexer.Create(new MeshMultiplexerOptions
{
    NodeId = "A",
    PoolId = "us"
});
mesh.Start();

// Add direct neighbors (mesh uses _mesh: channels; your own channels coexist)
mesh.AddNeighbor("B", muxToB, remotePoolId: "us");
mesh.AddNeighbor("C", muxToC, remotePoolId: "eu");

await mesh.WaitForReadyAsync();

// Open a routed sub-mux to node D (may traverse B → C → D)
await using var subMux = await mesh.OpenMultiplexerAsync("D", "rpc-1");

// Use exactly like any IStreamMultiplexer
var writer = subMux.OpenChannel(new ChannelOptions { ChannelId = "requests" });
await writer.WaitForReadyAsync();
await writer.WriteAsync(payload);

// Meanwhile, the same neighbor mux is still yours for direct traffic
var directWriter = muxToB.OpenChannel(new ChannelOptions { ChannelId = "my-app-data" });
await directWriter.WaitForReadyAsync();
await directWriter.WriteAsync(otherPayload);
```

The returned `IStreamMultiplexer` is a real `StreamMultiplexer` — same channel API, same transits, same backpressure, same statistics. The routing is invisible.

---

## How it works

### Sub-multiplexer construction

`StreamMultiplexer` needs a `StreamFactoryDelegate` returning `Task<IStreamPair>`. The core channel API provides everything:

- `IWriteChannel.AsStream()` → write-only `Stream`
- `IReadChannel.AsStream()` → read-only `Stream`
- `new StreamPair(readStream, writeStream)` → `IStreamPair`

```
A.OpenMultiplexerAsync("D", "rpc-1")

  1. BFS on adjacency map → next hop = B, path = [B, C, D]
  2. channelBase = "_mesh:route:D:A/rpc-1:0"
  3. Build MultiplexerOptions:
       StreamFactory = async ct => {
           var writer = muxToNextHop.OpenChannel(
               new ChannelOptions { ChannelId = channelBase + ">>" });
           var reader = muxToNextHop.AcceptChannel(channelBase + "<<");
           await Task.WhenAll(writer.WaitForReadyAsync(ct),
                              reader.WaitForReadyAsync(ct));
           return new StreamPair(reader.AsStream(), writer.AsStream());
       }
       SessionId = deterministic(A, D, "rpc-1")
       MaxAutoReconnectAttempts = MaxRouteRetries
       ConnectionTimeout = RouteTimeout
  4. StreamMultiplexer.Create(options), Start(), return IStreamMultiplexer
```

The trailing `:0` in the channel base is a monotonic nonce (incremented per open) that prevents collision if the same `(source, target, multiplexerId)` tuple is reused after a close.

### Relay forwarding

At B (intermediate relay):

```
  1. AcceptChannelsAsync on B↔A mux sees channel "_mesh:route:D:A/rpc-1:0>>"
  2. Parse prefix → target = D, source = A, id = rpc-1:0. Target != self → relay.
  3. BFS for next hop toward D → C
  4. On B↔A mux: accept the incoming read channel (>>), open the response write channel (<<)
  5. On B↔C mux: open forward write channel (>>), accept response read channel (<<)
  6. Splice both directions with raw ReadAsync/WriteAsync:
       Task.WhenAll(
           PipeAsync(incomingReader, forwardWriter, ct),   // A→B→C direction
           PipeAsync(responseReader, responseWriter, ct))  // C→B→A direction
```

The relay uses `ReadAsync` / `WriteAsync` directly on `IReadChannel` / `IWriteChannel` — no Stream wrapping, no allocation overhead per hop.

At D (terminus):

```
  1. AcceptChannelsAsync on D↔C mux sees channel "_mesh:route:D:A/rpc-1:0>>"
  2. Parse prefix → target = D = self. This is the terminus.
  3. Accept the read channel (>>), open the response write channel (<<)
  4. Construct StreamMultiplexer over it:
       StreamFactory = async ct => {
           // First call: use the channels that just arrived
           // Reconnect calls: await next incoming route for (A, rpc-1)
           var (reader, writer) = await awaitRouteChannels("A", "rpc-1", ct);
           return new StreamPair(reader.AsStream(), writer.AsStream());
       }
       SessionId = deterministic(A, D, "rpc-1")  // same as A's side
       MaxAutoReconnectAttempts = MaxRouteRetries
  5. Emit via AcceptMultiplexersAsync
```

The relay at B never parses NetConduit framing inside the routed stream. It sees opaque bytes. The sub-mux protocol runs end-to-end between A and D.

**Relay resource limits:** Each relay node tracks concurrent relay count. If `MaxConcurrentRelays` is reached, new relay requests are rejected (the channel is closed with an error). The opener's sub-mux will retry via StreamFactory, potentially finding an alternate path.

### Topology replication

Each direct neighbor gets a channel pair for topology exchange: write channel `_mesh:topo>>` and read channel `_mesh:topo<<`.

**Wire format:** 4-byte big-endian length prefix + UTF-8 JSON payload. The adjacency map is small (node count × ~100 bytes), so full-state exchange is used — no delta protocol needed.

State:

```csharp
internal sealed record AdjacencyMap(
    Dictionary<string, NodeEntry> Nodes);

internal sealed record NodeEntry(
    long Version,
    HashSet<string> Neighbors,
    string? PoolId);
```

**Merge rule:** For each `(nodeId, entry)` in the received map, take the entry with the higher `Version`. Only the owning node increments its own `Version`. This is a Last-Writer-Wins Register per node — commutative, associative, idempotent.

**Edge validity:** An edge A↔B is routable only when **both** `A.Neighbors` contains B **and** `B.Neighbors` contains A. Unilateral claims are ignored. This handles stale entries from crashed nodes: if A crashes, B runs `RemoveNeighbor("A")`, B's entry drops A, the edge becomes one-sided and unroutable. A's orphaned entry is harmless.

**Garbage collection:** Node entries with zero confirmed bidirectional edges for longer than `2 × PingInterval × MaxMissedPings` are dropped from the local map and the removal propagated.

**Flow on `AddNeighbor("B", muxToB, "us")`:**

1. Update local map: `self.Neighbors += B`, bump `self.Version`
2. Open topology channels on A↔B mux:
   - `mux.OpenChannel(new ChannelOptions { ChannelId = "_mesh:topo>>" })` → write to B
   - `mux.AcceptChannel("_mesh:topo<<")` → read from B
3. Send full map as length-prefixed JSON on the write channel
4. Background loop: read length-prefixed messages from the read channel
5. Merge each received entry by version. If local map changed → send full updated map to **all** other neighbor topology channels
6. Propagation converges when no map changes (usually 1-2 hops for simple topologies)

**Flow on `RemoveNeighbor("B")`:**

1. `self.Neighbors -= B`, bump `self.Version`
2. Close topology channels for B
3. Send updated map to remaining neighbor topology channels

**Reconnection:** When a neighbor mux reconnects (its `Connected` event fires), the topology channels resume via the mux's built-in replay. Both sides re-exchange full state on reconnect to ensure consistency.

### Routing

`BfsRouter` operates on the locally-known `AdjacencyMap`, considering only edges that are:

1. Bidirectionally confirmed (both entries agree)
2. The local end is `IsConnected` (transient drops are locally skipped, not broadcast)

Rules:

- Shortest path wins
- Tie-break: prefer paths whose intermediate nodes share `PoolId` with the source. Pool affinity **never adds hops** — it only breaks ties among equal-length paths
- Route results are cached and invalidated on `TopologyChanged`
- No route → buffer the pending sub-mux until topology converges or `RouteTimeout` elapses
- Hop count > `MaxHops` → throw `MeshRoutingException`

### Reroute on failure

When a relay node dies, the routed channel pair breaks. Both endpoints' sub-muxes detect disconnect.

**Opener side (A):** The sub-mux's `MaxAutoReconnectAttempts > 0`, so it calls its `StreamFactory` again. The mesh-managed factory runs BFS (topology may have updated), finds an alternate path, opens a new channel pair through different relays, and returns the new `StreamPair`. The sub-mux performs its reconnect handshake (SessionId match), replays unacknowledged frames, and resumes.

**Acceptor side (D):** Same mechanism. D's sub-mux calls its `StreamFactory`, which blocks until the mesh receives a new incoming route for `(A, "rpc-1")`. When A's new route arrives at D, the factory resolves and D's sub-mux performs the reconnect handshake.

**Deterministic SessionId** ensures both sides' reconnect handshakes match: `SessionId = SHA256(sort(sourceNodeId, targetNodeId) + multiplexerId)` truncated to a `Guid`.

**No alternate path available:** Both sides' `StreamFactory` calls time out after `RouteTimeout`. After `MaxRouteRetries` failures, sub-muxes give up and dispose. Application sees `Disconnected` event on the sub-mux.

**No reroute desired:** Set `MaxRouteRetries = 0`. Sub-muxes die immediately on route failure.

### Pending opens

`StreamMultiplexer` supports the "open before transport ready" pattern — channels sit in `Opening` state, writes buffer in the slab. The mesh reuses this:

- `OpenMultiplexer` synchronously returns an `IStreamMultiplexer` whose underlying `StreamFactory` has not yet resolved
- The user opens channels and writes data — everything buffers in the sub-mux's slab
- Once the route lands and both ends handshake, channels transition to `Open` and writes flush

No new pending-state machinery needed in core.

---

## Channel naming

The mesh reserves the `_mesh:` channel prefix on every neighbor mux.

| Channel ID | Direction | Purpose |
|---|---|---|
| `_mesh:topo>>` | Write to neighbor | Topology state (length-prefixed JSON) |
| `_mesh:topo<<` | Read from neighbor | Topology state (length-prefixed JSON) |
| `_mesh:route:{target}:{source}/{id}:{nonce}>>` | Write toward target | Routed byte pipe (outbound leg) |
| `_mesh:route:{target}:{source}/{id}:{nonce}<<` | Read from target | Routed byte pipe (inbound leg) |

The `>>` / `<<` suffixes follow the existing NetConduit transit convention for duplex channel pairs.

Validation: `MeshMultiplexer` rejects channel IDs starting with `_mesh:` opened by user code on channels it monitors via `AcceptChannelsAsync`. User code must not use the `_mesh:` prefix on neighbor muxes. All other channel names are unrestricted.

---

## Invariants

### What stays unchanged in core

| Component | Status |
|---|---|
| `StreamMultiplexer` | Unchanged |
| `IStreamMultiplexer` | Unchanged |
| Framing protocol | Unchanged |
| All transports | Unchanged |
| All existing transits | Unchanged |

The mesh is pure composition. `NetConduit.Mesh` uses only the core channel API (`OpenChannel`, `AcceptChannel`, `AsStream()`, `StreamPair`). No new framing, no new wire protocol.

### What does not belong in NetConduit.Mesh

These are the consumer's responsibility (e.g. GridConduit):

- Peer discovery (who to dial, how to find them)
- Authentication (who is allowed in the mesh)
- Authorization (which nodes can route to which)
- Reconnection policy at the neighbor level (when to drop or re-add a direct link)
- Custom gossip beyond topology (presence, metadata, application state)

`MeshMultiplexer` is a routing primitive. It assumes the caller already has `IStreamMultiplexer` connections to neighbors and knows their node IDs.

---

## Docs layout

```
docs/
    concepts/
        mesh.md              ← overview, routing, topology CRDT, pool affinity
    api/
        mesh-multiplexer.md  ← MeshMultiplexer / MeshMultiplexerOptions reference
    samples/
        mesh-3-node.md       ← A↔B↔C, open mux from A to C through B
```

`docs/packages.md` gains an entry for `NetConduit.Mesh`. `docs/concepts/index.md` links to the mesh concept page.

---

## Tests

Add `tests/NetConduit.Mesh.UnitTests/`:

| Test class | Coverage |
|---|---|
| `AdjacencyMapMergeTests` | CRDT merge: commutativity, associativity, idempotency, version wins, entry GC |
| `BfsRouterTests` | Shortest path, pool tie-break, hop limit, no route, disconnected-edge filtering, cache invalidation |
| `RouteChannelNamingTests` | Channel name generation, nonce uniqueness, prefix validation |
| `MeshRoutingTests` | 3-node line, 4-node diamond, end-to-end byte transfer through relay |
| `MeshRerouteTests` | Relay failure, alternate path reroute, sub-mux replay, deterministic SessionId matching |
| `RelayLimitTests` | MaxConcurrentRelays enforcement, rejection, fallback to alternate path |
| `TopologyConvergenceTests` | Concurrent topology changes, eventual convergence, stale entry GC |
| `ChaosTests` | Random add/remove neighbors under load, open sub-muxes during topology churn |

Existing tests from GridConduit's internal mesh (`AdjacencyMapTests`, `BfsRouterTests`, `MeshRoutingIntegrationTests`, etc.) port with namespace changes.

---

## Migration path for GridConduit

```diff
- using GridConduit.Internal;
+ using NetConduit.Mesh;

- IStreamMultiplexerMesh mesh = new StreamMultiplexerMesh(nodeId);
+ var mesh = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = nodeId });
+ mesh.Start();

- mesh.AddNodeMultiplexer("B", mux);
+ mesh.AddNeighbor("B", mux);

- IStreamMultiplexer sub = await mesh.OpenStreamMultiplexerAsync("D", "rpc-1");
+ IStreamMultiplexer sub = await mesh.OpenMultiplexerAsync("D", "rpc-1");
```

GridConduit deletes its internal mesh implementation. Discovery, auth, and gossip layers stay in GridConduit.

---

## Resolved questions

1. **`IStreamMultiplexer` interface** — exists at `src/NetConduit/Interfaces/IStreamMultiplexer.cs`. The mesh returns `IStreamMultiplexer` directly. No changes to core needed.

2. **Sub-mux SessionId** — deterministic from `SHA256(sort(nodeA, nodeD) + multiplexerId)` truncated to `Guid`. Both sides compute the same value. Survives path changes (reroute through different relays).

3. **GoAway propagation** — `MeshMultiplexer.GoAwayAsync()`:
   - Calls `GoAwayAsync()` on all active routed sub-muxes
   - Closes all topology channels
   - Does **not** touch the neighbor muxes themselves — their lifecycle belongs to the caller
   - Neighbor muxes remain fully usable for non-mesh traffic after the mesh shuts down

4. **Stats** — `MeshMultiplexer.Stats` returns `MeshStats`:
   ```csharp
   public sealed class MeshStats
   {
       public int ActiveSubMultiplexers { get; }
       public int ActiveRelays { get; }
       public long RoutesOpened { get; }
       public long RoutesFailed { get; }
       public long RelayBytesForwarded { get; }
       public long TopologyDeltasSent { get; }
       public long TopologyDeltasReceived { get; }
   }
   ```
