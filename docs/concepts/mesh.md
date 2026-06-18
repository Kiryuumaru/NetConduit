# Mesh multiplexer

`NetConduit.Mesh` builds a routed overlay on top of `StreamMultiplexer`. Each
node owns one `MeshMultiplexer` and registers its direct neighbor connections
by passing `MultiplexerOptions` — the same options type that
`StreamMultiplexer` uses to create transports via `StreamFactory`. The mesh
replicates a small topology, runs BFS to pick the next hop, and opens a
*sub-multiplexer* tunneled through that next hop. The result is a full
`IStreamMultiplexer` API to any reachable node by ID.

```
A ----- B ----- C
                |
                D
```

If A opens a routed multiplexer to D, the mesh layer:

1. Looks up the route `A -> B -> C -> D` in its replicated topology.
2. Opens a dedicated channel on the `A-B` mux, named `_mesh:route:<sid>`.
3. Asks B to keep relaying: B opens `_mesh:route:<sid>` on `B-C`, C does the
   same on `C-D`. Each hop pumps bytes between the two channels with a
   `RouteForwarder`.
4. A and D wrap their endpoint channels in a fresh `StreamMultiplexer`. Both
   sides have a full multiplexer API on a single routed pipe.

## Partial mesh

The mesh layer only reserves channel IDs prefixed with `_mesh:`. Everything
else on the neighbor mux remains available to the application. You can run
mesh traffic alongside your own channels on the same `StreamMultiplexer`
without coordination.

### Adding neighbors

A neighbor is a `StreamMultiplexer` connected to another node:

```csharp
// B dials A; A accepts the connection
meshB.AddNeighbor("A", TcpMultiplexer.CreateOptions("192.168.1.1", 9001));
meshA.AddNeighbor("B", TcpMultiplexer.CreateServerOptions(listener));
```

The mesh calls `StreamMultiplexer.Create()` internally, starts it, and
manages its lifecycle — same pattern as `StreamMultiplexer` itself, where
`MultiplexerOptions` carries a `StreamFactory` that builds the transport.

For cases where you want to share the mux between mesh traffic and your own
application channels on the same TCP connection, pass a pre-built
`IStreamMultiplexer` instead.  The mesh never disposes a mux you hand it.

```csharp
var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("host", 9001));
mux.Start();
await mux.WaitForReadyAsync(ct);
mesh.AddNeighbor("B", mux);   // mesh coexists with your app:foo channels on this mux
```

Both overloads converge on the same internals — the difference is who owns
the mux lifetime.

## Topology

Each node owns a `NodeEntry { NodeId, Sequence, Neighbors }`. Entries are
exchanged as JSON frames on a per-sender channel
`_mesh:topo:from:<senderNodeId>` and merged using last-writer-wins on
`Sequence`. The result is a CRDT-style adjacency map that converges as long as
the underlying neighbor muxes deliver frames in order.

`TopologyChanged`, `NodeReachable`, and `NodeUnreachable` events fire whenever
the adjacency map produces a different reachable set.

## Routing

`BfsRouter` runs BFS on the merged adjacency map. The first hop is the
neighbor; downstream hops are encoded in the `_mesh:route:` channel options so
each relay can forward without re-running BFS.

`MeshMultiplexerOptions.MaxHops` caps the path length. A request with a longer
path is rejected at the source with `MeshRoutingException`.

## Pool affinity

The mesh layer never owns the neighbor `StreamMultiplexer`s. Disposing the
mesh leaves neighbor muxes running so the application can keep its own
channels on them. Conversely, killing a neighbor mux only invalidates the
routes that pass through it; other routes stay healthy.

## Test plan coverage

The integration tests under `tests/NetConduit.Mesh.IntegrationTests/` cover:

- two-node open / accept / round-trip
- three-node line with multi-hop relay
- `MaxHops` enforcement
- lifecycle (create, start, dispose, idempotency)
- coexistence with user channels on the same neighbor mux
- statistics (routes opened, topology messages, relay bytes)

## Options reference

`MeshMultiplexerOptions` is a record with these fields. Defaults preserve
backward-compatible behavior — every advanced knob is opt-in.

| Option | Default | Purpose |
| --- | --- | --- |
| `NodeId` | (required) | Stable identity for this node in the topology map. |
| `PoolId` | `null` | Optional grouping tag advertised with this node's entry. |
| `MaxHops` | `10` | Reject routes longer than this at the source. |
| `RouteTimeout` | `30s` | How long a route open will wait for a path. |
| `MaxRouteRetries` | `3` | Retry budget for the routed sub-mux when its transport dies. `-1` = unbounded. |
| `MaxConcurrentRelays` | `100` | Cap on relay slots this node hosts as an intermediate. |
| `RecomputeDebounce` | `Zero` | Coalesce N rapid topology updates into one BFS. `Zero` preserves the synchronous recompute path. Non-zero values can stale the route table during an active reroute — only enable if you actually have churn. |
| `TopologyAntiEntropyInterval` | `Zero` | Periodic re-broadcast of local topology to recover from silently-dropped frames. `Zero` disables. Pairs cleanly with `RecomputeDebounce > 0` when running at scale. |
| `DefaultSlabSize`, `PingInterval`, `PingTimeout`, `MaxMissedPings`, `GoAwayTimeout`, `DefaultChannelOptions` | (StreamMultiplexer defaults) | Forwarded to every routed sub-mux. |
| `MaxTopologyMessageSize` | `1 MiB` | Hard cap on a single inbound topology frame. |

### `MaxRouteRetries = -1` — unbounded reroute

With the default `3`, the routed sub-mux raises terminal `Disconnected`
once its underlying transport dies three times in a row. The user-visible
mux dies and the application has to reopen.

Setting `MaxRouteRetries = -1` flips the sub-mux into unbounded
auto-reconnect. The route opener keeps consulting BFS forever; as long
as ANY path exists between source and target, the sub-mux stays alive.
Useful for long-lived RPC sessions where the application would rather
hang a request than tear down state.

Caveats:

- An "unreachable" target (no path in the topology map) blocks an open
  forever instead of raising `MeshRoutingException`. Bound your opens
  with a `CancellationToken` if you need a timeout.
- Counters that track `RoutesFailed` now count retries, not terminal
  failures. Drives different telemetry expectations.
- **Reroute is not seamless mid-stream.** When the sub-mux reconnects
  along a different path, channels rely on `StreamMultiplexer`'s replay
  semantics. The writer replays its entire send slab; the reader skips
  bytes it already received so no duplicates are delivered to the
  application. Channels opened during the outage may be reopened
  after recovery. The sub-mux *identity* survives the reroute, but
  applications must tolerate the same reconnect/reopen semantics that
  apply to a single-link `StreamMultiplexer` reconnect.

### Path stickiness and direct-link health

The router picks the shortest path from the current topology snapshot.
Only **healthy** neighbors appear in a node's advertised adjacency list,
so BFS naturally routes around unhealthy links. When a neighbor mux
disconnects, the local node removes it from its advertised neighbors and
broadcasts immediately; peers recompute routes within one topology
convergence round.

A freshly-recovered neighbor is re-advertised once its mux fires
`Connected`. Routes may shift back to the shorter path at that point.
Use `MaxRouteRetries = -1` on the routed sub-mux if you want opens to
ride out the re-stabilization rather than fail.

### `RecomputeDebounce` — coalesced BFS

Every `OnTopologyMessageReceived` immediately runs BFS over the full
graph, rebuilds the adjacency snapshot, broadcasts to every neighbor,
and fires `TopologyChanged`. In a small mesh during convergence this is
cheap. In a 100-node mesh under churn it cascades.

Setting `RecomputeDebounce = TimeSpan.FromMilliseconds(50)` (or higher)
collapses N updates received in that window into a single BFS +
broadcast. Convergence latency goes up by at most the debounce window;
CPU under churn drops dramatically.

Leave at `Zero` for tests and small deployments — the synchronous path
keeps routes hot during active reroute scenarios.

### `TopologyAntiEntropyInterval` — periodic re-broadcast

Independent of debounce. When set to a positive interval, every node
re-broadcasts its full local topology snapshot at that cadence. Recovers
from any silently-dropped frame the read loop didn't surface. The
single-flight write queue ensures this never floods a neighbor — a
pending write just replaces the prior one (last-writer-wins).

5 minutes is a reasonable starting point at large scale. Leave at `Zero`
in development.

## Self-healing behavior

Neighbor registration is **sticky**. The mesh never auto-removes a
neighbor — `RemoveNeighbor` is reserved for explicit, permanent intent.

The mesh layer subscribes to each neighbor mux's `Disconnected` and
`Connected` events. When a neighbor mux disconnects (whether mid-reconnect
or terminal), the mesh marks that session **unhealthy**, removes the
neighbor from its *advertised* adjacency list, bumps local topology
version, broadcasts the update, and runs BFS. Peers that receive the
update route around the unhealthy link. If the neighbor was the only path
to some node, `NodeUnreachable` fires.

On `Connected` (a recovered neighbor mux), the mesh marks the session
healthy again, re-adds the neighbor to the advertised adjacency, bumps
version, broadcasts, and re-runs BFS. Routes that were previously
diverted may shift back to the recovered link.

When using the `IStreamMultiplexer` overload of `AddNeighbor`, the
application owns the mux lifetime — health transitions never dispose it.
