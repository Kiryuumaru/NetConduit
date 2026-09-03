# Mesh: Every Node Reaches Every Node

**Elevator pitch:** Core NetConduit gives you one fast pipe to one peer. Mesh keeps that exact feel, but removes the one-peer limit — you name any node anywhere in the group and get what looks like a direct pipe to it, while the mesh quietly hops your bytes across intermediate nodes to get there.

## Starting From Core

- On master, NetConduit is **point-to-point**. One multiplexer talks to one peer over one connection.
- On top of that connection you open **channels** — lightweight streams sharing the same pipe. Simple, fast, predictable.
- The wall is obvious: if you want ten peers, you manage ten pipes. If peer D is only reachable through B and C, you do the forwarding yourself.

Mesh knocks down that wall. You still open channels on something that feels like a normal core mux. You just stop caring who is directly connected to whom.

## What Mesh Is

Mesh is a **routing overlay** sitting on top of ordinary core connections.

- You keep your direct neighbor links exactly as before.
- Mesh layers a map, a router, and a relay system over them.
- Apps see **named destinations** instead of sockets and addresses.
- Bytes **hop** node to node until they arrive.

Nothing about core changes. Mesh just chains core pipes together and hides the chain.

## The Cast

- **Node** — a named participant in the mesh. Has an identity like A or D, and a view of the wider group.
- **Neighbor** — a direct link between two nodes, built from ordinary node-to-node muxes. The only real connections in the system.
- **Topology map** — every node's picture of who sees whom. Versioned so newer news wins, and constantly gossiped between neighbors until everyone agrees.
- **Route** — the shortest hop path between two nodes, found by breadth-first search over the topology map. Paths that stay in the same pool are preferred when pools are in use.
- **Opener session** — the side that dials. When your app asks for node D, the opener figures out the route and starts building it hop by hop.
- **Acceptor session** — the side that receives. Node D hears a knock, accepts, and hands your app a fresh mux as if you dialed it directly.
- **Relay** — what a middle node does. It holds two legs open and pumps bytes leg to leg without interpreting them, while counting what it forwarded.
- **Routed sub-mux** — the end-to-end virtual mux that A and D each see. To the app it behaves like a normal direct core mux, with channels, ordering, and reconnect behavior.
- **Route channels** — the per-hop carriers. Each hop gets a specially named channel pair that carries one leg's bytes, chained together into the full path.

## How It Flows

### 1. Joining — from lonely node to reachable mesh

- A node starts with a name and zero neighbors.
- It adds one or two neighbor links to nodes already in the mesh.
- Neighbors swap topology maps immediately.
- Each map is re-gossiped outward, version by version.
- Within a few rounds the maps **converge** — everyone sees roughly the same graph.
- Once a path exists in the map, the far node becomes **reachable** and apps can open to it.

  Joining:

      A stands alone

      A -- adds link -- B -- already linked -- C -- D

      maps flow: A <-> B <-> C <-> D

      result: A can see D, even with no direct wire

### 2. Opening A to D — asking by name, getting a mux

- Your app on A asks for **node D by name**, not by address.
- Mesh resolves the current best route, say A to B to C to D.
- It opens a route channel pair on the A to B hop.
- It asks B to extend the chain, B opens B to C, C opens C to D.
- A and D each wrap their endpoint in a **routed sub-mux**.
- Both apps now talk as if A and D were directly wired.

  Opening:

      App on A -- "talk to D" --> mesh on A

      A --hop--> B --hop--> C --hop--> D

      A [virtual mux] ================== D [virtual mux]
               chained legs underneath

### 3. Relaying — the middles pump bytes

- B and C each hold two open legs for the same end-to-end session.
- Bytes arriving on the left leg are pushed out the right leg, and back the other way.
- Relays do **not** parse messages or peek at channels — just forward.
- Each relay counts forwarded bytes for observability.
- Add more sessions and you add more independent pumps; each node hosts a bounded number at once.

  Relaying:

      A --> [B: left leg ==> right leg] --> [C: left leg ==> right leg] --> D

      B and C never interpret, only forward

### 4. Churn — links die, heal, and routes move

- Direct links flap. That is normal life, not an emergency.
- When B to C dies, B and C update their local maps, bump versions, and gossip the bad news.
- Everyone **recomputes routes** around the hole. A to D might swing through E instead.
- Stranded sub-muxes ride out short outages using core replay and reconnect along new paths.
- When the dead link heals, maps update again and future routes may swing back to the shorter path.

  Churn:

      Before: A --> B --> C --> D

      B--C breaks, news gossips, recompute

      After:  A --> B --> E --> D

      sub-mux identity survives, path underneath changes

### 5. Leaving — graceful goodbye versus silent death

- **Graceful leave:** the node tells its neighbors it is going, neighbors update maps at once, routes drain cleanly.
- **Abrupt death:** no goodbye. Neighbors notice the dead link, mark it unhealthy, gossip the loss, and the departed node fades from the map.
- Either way the mesh does not need a central registry — absence of news plus dead links is how it notices.
- Rejoin later with the same name and you simply converge again like a fresh join.

  Leaving:

      Graceful: D says bye --> C gossips --> all drop D at once

      Abrupt:   D vanishes, C notices dead leg --> gossips loss --> all route around

## What It Guarantees (And Does Not)

**You can count on:**

- **Name-based reachability** — if a path exists in the map, you can open to it by name.
- **Core-like feel** — the sub-mux behaves like the direct mux you already know.
- **Automatic detour** — topology gossip plus shortest-path routing steers around dead links.
- **Coexistence** — mesh traffic shares neighbor pipes with your normal app channels.
- **Bounded relay load** — no single node is asked to relay without limit.

**You must design for:**

- **Reroute is not seamless mid-stream** — reconnect replays, and channels open during an outage may reopen after recovery.
- **Unreachable targets block** — opening to a node with no known path waits until cancelled, so bound opens with a timeout.
- **Single-node mesh never becomes ready** — with nobody to gossip with, there is nothing to converge on.
- **Relay capacity is bounded** — defaults to one hundred concurrent relays per node, extra relay requests are refused.

## Where It Stands

Early feature on a draft branch — core routing, relay, and gossip are wired and tested, with hardening still in progress.

## TL;DR

- **Mesh turns many point-to-point pipes into one named neighborhood where any node can open what feels like a direct mux to any other node.**
- **Middle nodes just forward bytes, maps gossip until everyone agrees, and routes heal around failures.**
- **Expect reconnect semantics, use timeouts for unknown targets, and size relay capacity for your shape.**
