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

`tests/NetConduit.Mesh.UnitTests/`:

| Test class | Coverage |
|---|---|
| `AdjacencyMapMergeTests` | CRDT merge: commutativity, associativity, idempotency, version wins, entry GC |
| `BfsRouterTests` | Shortest path, pool tie-break, hop limit, no-route, disconnected-edge filtering, cache invalidation |
| `RouteChannelNamingTests` | Channel name generation, nonce uniqueness, prefix parsing |
| `MeshRoutingTests` | 3-node line, 4-node diamond, end-to-end byte transfer through relay |
| `MeshRerouteTests` | Relay failure, alternate path reroute, sub-mux replay, deterministic SessionId matching |
| `RelayLimitTests` | MaxConcurrentRelays enforcement, rejection, fallback to alternate path |
| `TopologyConvergenceTests` | Concurrent topology changes, eventual convergence, stale entry GC |
| `PartialMeshTests` | User channels coexist with `_mesh:` channels on the same neighbor mux |
| `ChaosTests` | Random add/remove neighbors under load, open sub-muxes during topology churn |
