# Mesh multiplexer

`NetConduit.Mesh` builds a routed overlay on top of `StreamMultiplexer`. Each
node owns one `MeshMultiplexer` and supplies a fully connected
`StreamMultiplexer` for each direct neighbor. The mesh layer replicates a small
topology, runs BFS to pick the next hop, and opens a *sub-multiplexer* tunneled
through that next hop. The user keeps the familiar `StreamMultiplexer` API on
the resulting routed mux.

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
path is rejected at the source with `MeshRouteException`.

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
