# Reconnection Feature Analysis

## Feature Decomposition

Mux-level reconnection breaks down into 4 distinct layers with fundamentally different cost profiles.

### Layer 1 — Transport Recreation via Factory

- Factory sits idle until disconnect
- Zero per-send overhead, zero per-receive overhead
- Fires on failure as a callback
- Competitors (yamux, smux, quic-go) lack this — a gap in their design
- **Cost: Zero hot-path impact**

### Layer 2 — Transport Creation Delegation

- TCP, WebSocket, etc. creation handled by mux factory
- Mux can recreate the transport independently
- Control-plane only, no data-path cost
- **Cost: Zero hot-path impact**

### Layer 3 — Channel Reattachment + Disconnect Notification

- Channel registry (ID → channel) already exists for frame routing
- On reconnect: handshake says "I had channels 1, 5, 7", remote side remaps
- Happens once per reconnect event, not per send
- Channels get notified of disconnect/reconnect via events
- **Cost: Near-zero hot-path impact**

### Layer 4 — Byte Backtracking / Replay Buffer

- Every `Send()` copies bytes into ring buffer under lock
- On reconnect, replays what remote side missed
- This is `ChannelSyncState` recording — lock + memcpy
- Measured cost: 31-77% throughput drop when active
- **Cost: 100% of the observed slowdown**

---

## Layer 4 Deep Analysis

### Why Layer 4 Is Expensive

- Touches every single `Send()` call on the hot path
- Requires `lock(_lock)` + memcpy into 1MB ring buffer per write
- Penalizes 100% of sends for something that happens <0.01% of the time

### Arguments Against Mux-Level Replay

- Application protocols almost always have their own ack/retry semantics (request-response, sequence numbers, idempotency tokens)
- Silently replaying bytes behind the app's back can be worse than a clean "you disconnected, resend from X"
- TCP already provides byte-level reliable delivery within a connection — Layer 4 reimplements TCP reliability across connections
- Ring buffer has fixed size — once it overflows, guarantee is lost anyway
- Not fully reliable = worst of both worlds (app can't trust it, but still pays the cost)

### Arguments For Mux-Level Replay

- Transparent recovery — channels need no gap-handling code
- Simple programming model — "it just works" like a brief phone dropout
- Fire-and-forget protocols (telemetry, logging) have no app-level retry logic

---

## Architectural Options

### Option A: Remove Layer 4 Entirely

- Layers 1-3 always on (zero cost)
- Layer 4 removed — apps handle their own replay/retry
- Matches competitor architecture (yamux, smux, quic-go are all stateless muxes)
- Eliminates hot-path overhead permanently
- Simplest implementation, biggest code reduction

### Option B: Layer 4 Opt-In Per Channel

- Layers 1-3 always on (zero cost)
- Layer 4 opt-in at channel open time, not per mux
- Channels that need replay say so; channels that don't pay nothing
- Example: chat app enables replay on message channel, not on typing-indicator channel
- File transfer app disables it entirely (has own checksums/resume)
- Granular cost — only channels that need it pay for it

### Option C: Layer 4 Per Mux (Current Design)

- All-or-nothing at mux level (`EnableReconnection` option)
- Every channel in the mux pays the recording cost
- `ReconnectBufferSize=0` trick makes recording a no-op but then no actual replay data
- Current source of all benchmark slowdowns in Plans 069-072

---

## Open Questions

- Should Layer 4 exist at all, or should replay be the application's responsibility?
- If Layer 4 exists, per-channel or per-mux granularity?
- Can Layer 4 be made zero-cost when disabled at per-channel level? (likely yes — no recording = no lock)
- Is there a design where Layer 4 recording doesn't require a lock? (append-only ring buffer with atomic pointer?)

---

## Plan History (Removing EnableReconnection Option)

| Plan | Approach | Result |
|------|----------|--------|
| 069 | Remove option, always-on, ReconnectBufferSize=0 no-op | Failed — benchmark variance |
| 070 | Same approach, retry | Failed — benchmark variance |
| 071 | Same approach, retry | Failed — benchmark variance |
| 072 | Re-baseline docs/benchmarks.md + Plan 071 changes | Failed — within-session variance |

All 4 plans used the same core technique: make `ChannelSyncState.RecordSend` a no-op via `Interlocked.Add` fast path when `ReconnectBufferSize=0`. Failed due to Go competitor runtime variance in benchmarks, not due to actual NC regression.
