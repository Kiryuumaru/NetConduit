# GRAND_FIX — NetConduit Architectural Diagnosis & Proposal

> Status: proposal. No code in this PR — review the plan before sequencing implementation PRs.

## TL;DR

The automatic bug-finder agent isn't outpacing fixes because of **architectural fragility**, not coding mistakes. **41 open issues collapse into 8 cross-cutting root causes**. Every patch shipped now only addresses **one site of one root cause** — there are 4–6 other sites for each. The bug-finder will keep producing isomorphic issues until those 8 root causes are eliminated by structural changes.

The same patterns repeat verbatim across the issue tracker:

- *"`X` uses throwing `WriteRawFrame` — control-slab pressure faults reader (parallel of #291/#336/#355/#365/#373/#377)"* — **9 issues, one root cause, six sites**.
- *"`X`.\_disconnectedFired / \_readyTcs / one-shot latch never reset on reconnect (mux-level analogue of #371/#396/#400)"* — **at least 12 issues, one root cause, ~12 sites**.

---

## Diagnosis: the 8 root causes

### RC1 — The control channel uses a fail-fast (throwing) write API on hot read paths

**Symptom cluster:** #291, #336, #355, #365, #373, #374, #377, #392, #404 (≈ 9 issues).

The control channel is a fixed-size slab; `WriteRawFrame` throws `InvalidOperationException("Slab full")` when full. It is called by:

- Reader thread for `INIT-ACK`, `PONG`
- Keepalive for `PING`
- `GoAwayAsync` for the local-shutdown frame
- ReadChannel cumulative ACK for backpressure

Any one of these throwing faults the **reader task**, kills the mux, masquerades as a transport error, and `GoAwayAsync` leaves the mux in half-shutdown limbo. Partial conversions to `TryWriteRawFrame` have already landed for ACK and PING (#291/#336/#355), but the rest of the call sites kept the throwing variant. **Every new control-frame path added starts in the throwing camp by default.**

### RC2 — Lifecycle events are modeled as scattered boolean latches, not state-machine edges

**Symptom cluster:** #370, #371, #378, #381, #382, #383, #391, #395, #396, #400, #401, #405 (≈ 12 issues).

The mux carries `_isRunning`, `_isConnected`, `_isShuttingDown`, `_isReady`, `_disconnectedFired`, `hasConnectedBefore`, plus per-transit `_connectedFired`, plus per-channel similar latches. They get mutated from ~7 sites in `MainLoopAsync` alone. Resetting one across reconnect is just a discipline; nobody enforces it. The result:

- `Disconnected` fires twice on bidirectional transits (#370, #381, #382, #405)
- `Disconnected` never fires on `DisposeAsync` after a reconnect (#383, #400)
- `Disconnected` never fires on `GoAwayAsync` (#378)
- `Disconnected` never fires on dispose-before-handshake (#395, #401)
- `Ready` ignores the read side (#379)

Every bug here is "we forgot to set X = false at site Y." There are infinite Ys.

### RC3 — Transit-level events leak through from underlying channels with no coalescer

**Symptom cluster:** #370, #371, #381, #382, #396, #405 (≈ 6 issues, overlapping with RC2).

`MessageTransit`, `DeltaMessageTransit`, `StreamTransit`, `DuplexStreamTransit` all compose **two channels** but expose **one** `Connected`/`Disconnected`/`Ready` triple. They subscribe to each channel's event and forward — so a bidirectional transit emits `Connected` twice. Each transit has independently re-derived its own (broken) coalescing logic. Fixes land per-transit (#357 → #371 → #396 → #405) and the next sibling immediately regresses.

### RC4 — `DisposeAsync` returns resources before all consumers have stopped touching them

**Symptom cluster:** #368, #376, #384, #390, #397, #398, #403 (≈ 7 issues).

Dispose phase ordering is wrong or undefined:

- Slab returned to `ArrayPool` while the writer thread is mid-`writeStream.Write(frames.Span)` (#368 — use-after-free corrupting wire bytes)
- `ArrayPool` slabs leaked when `RegisterWriteChannel` throws (#390) or when batch register rolls back (#384)
- Kernel handles (`_cts`, `_readySignal`, `_flushSignal`) leaked when mux never started (#376)
- Timer leaks per successful pong (#397)
- WebSocket pairs leaked on dispose (#398)
- IPC server `FileStream` leaked (#403)

The common cause: **no defined dispose ordering invariant.** Each component knows how to free its own resources but not the global "stop, drain, then release" sequence.

### RC5 — Channel registry doesn't auto-unregister on terminal channel state

**Symptom cluster:** #237 (recently fixed by PR #326), #367, #385, #399.

`ChannelRegistry` separates "channel exists in `_writeChannels`/`_readChannels`/`_idToIndex` maps" from "channel is in a terminal state." When a channel reaches `Closed` (RemoteFin, LocalClose), the WriteChannel side auto-unregisters via `NotifyChannelCompleted`, but the ReadChannel side does not (#367). Pre-handshake channels never reach a terminal state if initial connect fails (#385). The result: peer-legal "close + reopen same ID" faults the mux. The same registry is also the source of #237 (parity-reassign race) that PR #326 patched with a lock — the underlying separation-of-concerns problem remains.

### RC6 — Cancellation vs transport-fault are conflated at exception types

**Symptom cluster:** #375, #380, #388.

- UDP swallows caller CT → surfaces as `TimeoutException` (#375)
- `DeltaMessageTransit` cancellation mid-frame permanently desyncs framing (#380)
- Disposing during in-flight `Send`/`Receive` masks the real channel exception with `ObjectDisposedException` (#388)

There's no taxonomy distinguishing "caller asked to cancel" from "transport died" from "mux disposed underneath you." Every operation path makes up its own answer.

### RC7 — Retry loop classifies every exception as transient

**Symptom cluster:** #386, #402, #406.

`MuxConnectRetry` has a single `catch (Exception ex)` that retries everything. Fatal faults (one-shot server-side factory's `InvalidOperationException`, refused IPC connections during startup, zero-delay configs) just spin forever. There's no `TransportFault { Fatal | Transient }` discriminator.

### RC8 — Backpressure model is broken at the wire

**Symptom cluster:** #394.

`MaybeSendAck` reports **wire-received** position, not **consumer-consumed** position. The documented "slow consumer slows producer" contract is unenforced; instead a slow consumer crashes the receiver via `ProtocolError`. This is one issue today, but it's the **kind** of issue that's load-test latent — the bug-finder will keep producing variants once it stress-tests with slow consumers.

---

## Proposed architectural solution

Eight structural changes. Each kills an entire root-cause class, **not one site**. After this, the bug-finder will start producing *new* shapes of bugs (the interesting kind), not isomorphic copies of the old ones.

### S1 — Replace the control channel slab with a non-throwing bounded ring + overflow policy

**Kills RC1 (9 issues).**

The control channel is fundamentally different from data channels: control frames are small (≤32 B), bounded in arrival rate by RTT/keepalive interval, and **must never fail the mux from local pressure**. Replace its slab with:

- A purpose-built `ControlFrameQueue` — small fixed ring, **single** API `Enqueue(frame)` returning `bool`, never throws.
- Overflow policy: drop only **coalescable** frames (ACK with stale position, PING when one is pending). Required frames (INIT-ACK, GoAway, PONG-correlated) always succeed because they're bounded by peer's outstanding open/keepalive count, which is bounded.
- Reader/keepalive/dispose paths call `Enqueue` and on `false` either coalesce (ACK), retry next tick (PING), or escalate to "tear down transport gracefully" (INIT-ACK only after exceeding peer's open-budget, which is a protocol violation by them).
- Delete `WriteRawFrame`'s throwing path — `TryWriteRawFrame` becomes the only API.

This single change eliminates every "reader thread fault on control-slab pressure" issue and removes the entire pattern from the bug-finder's surface.

### S2 — Single `MuxState` state machine with declarative event edges

**Kills RC2 (12 issues).**

Replace `_isRunning`, `_isConnected`, `_isShuttingDown`, `_isReady`, `_disconnectedFired`, `hasConnectedBefore` with one type:

```csharp
enum MuxState { Idle, Connecting, Handshaking, Connected, Reconnecting, Draining, Disposed }
```

A single `Interlocked.Exchange`-driven `TransitionTo(MuxState next)` is the **only** mutator. Transitions fire events as a declarative table:

| From → To | Event |
|---|---|
| Connecting → Connected (first) | Connected, Ready |
| Reconnecting → Connected | Connected |
| Connected → Reconnecting | Disconnected(TransportError) |
| Connected → Draining (local) | (nothing — wait for Drained) |
| Draining → Disposed | Disconnected(LocalDispose) |
| Connecting → Disposed (pre-handshake) | Disconnected(LocalDispose) |
| Reconnecting → Disposed (terminal retry) | Disconnected(TransportFailed) |

The state machine **owns** event firing. No call site raises `Connected`/`Disconnected` directly. Each transition is logged for debuggability. Resetting `_disconnectedFired` is impossible to forget because there is no `_disconnectedFired`.

### S3 — Transit event coalescing base class

**Kills RC3 (6 issues, overlap with RC2).**

Move the `Connected = AND of N channels`, `Disconnected = OR of N channels`, `Ready = AND` logic into a single `MultiChannelEventCoalescer<TChannel>` used by all four transits. Channel subscriptions are wired by the base; derived transits only declare *which* channels are members. Bidirectional transit defines members = `{write, read}`; unidirectional defines members = `{the one channel}`. The duplicate-firing bug becomes structurally impossible.

### S4 — Phased `DisposeAsync` with explicit drain barrier

**Kills RC4 (7 issues).**

Define a strict dispose protocol on `StreamMultiplexer`:

```text
Phase 1 (gate):    state := Draining (rejects new OpenChannel/AcceptChannel)
Phase 2 (signal):  _cts.Cancel(); _flushSignal.Signal()
Phase 3 (await):   await writer; await flusher; await reader; await keepalive  // hard barrier
Phase 4 (channels):AbortAllChannels(MuxDisposed) — channels transition to terminal
Phase 5 (release): return slabs to ArrayPool; dispose CTS / signals / timers
Phase 6 (event):   raise Disconnected(LocalDispose) via state machine
```

**Phase 3 is the barrier:** no slab/handle release happens until every task that could touch them has exited. Writer's `writeStream.Write(frames.Span)` cannot race with ArrayPool return because the return is gated on writer-completion. Each channel owns its own slab and is responsible for its own ArrayPool return *only after* its writer-task is observed exited.

Add a `DisposeGuard` helper that asserts every slab/handle is owned by a phase and released exactly once. Phase violations throw in DEBUG; in RELEASE the guard is a no-op.

### S5 — Promote `ChannelRegistry` to own channel lifecycle, not just lookup

**Kills RC5 (4 issues including #237 cleanup).**

Today the registry is a glorified `ConcurrentDictionary`. Promote it to the authoritative owner of channel state transitions:

- `RegisterChannel(channel)` returns a handle. Channel state changes go through the registry (`MarkOpen`, `MarkClosed`, `MarkAborted`). The registry **auto-unregisters** on terminal state — symmetric for Read and Write channels. Fixes #367.
- The registry exposes `ReserveOutboundIndex()` returning a token; the token holds the lock until `Commit(channel)` or `Rollback()`. No more "allocate + register + write INIT" three-step that opens races (#237, #390). Atomic by construction.
- Pre-handshake channels enter a `PendingParity` state until handshake decides parity; the registry handles parity-aware allocation internally. The whole `ReassignPreHandshakeWriteChannelIndices` walk goes away.
- Failed-handshake terminal state transitions all `PendingParity` channels to `Aborted(HandshakeFailed)`. Fixes #385.

### S6 — Typed cancellation taxonomy

**Kills RC6 (3 issues + future load-test bugs).**

Introduce three sources of cancellation, each with a distinct exception type and a single conversion table:

| Source | Token | Surfaced as |
|---|---|---|
| Caller CT | `userCt` | `OperationCanceledException(userCt)` |
| Transport death | `transportCt` | `MuxTransportException` |
| Mux dispose | `muxCt` | `MuxDisposedException` |

Every operation builds a `CancellationTokenSource.CreateLinkedTokenSource(userCt, transportCt, muxCt)` and catches `OperationCanceledException`, then **dispatches** based on which source token was cancelled. Frame-protocol code (DeltaMessage, Message) uses a `using var frame = reader.BeginFrame()` guard whose `Dispose` either commits or rolls back the read cursor — partial-frame desync (#380) becomes structurally impossible.

### S7 — Typed transport-fault result + retry classifier

**Kills RC7 (3 issues).**

Change `StreamFactory` return contract from `Task<IStreamPair>` (with `Exception` for failures) to `Task<TransportConnectResult>`:

```csharp
sealed record TransportConnectResult
{
    public sealed record Success(IStreamPair Pair) : TransportConnectResult;
    public sealed record Transient(Exception Reason) : TransportConnectResult;
    public sealed record Fatal(Exception Reason) : TransportConnectResult;
}
```

`MuxConnectRetry` switches on the result; `Fatal` immediately terminates the mux with `Disconnected(TransportFailed, reason)`. Existing transport helpers declare their one-shot factories as returning `Fatal` on the second invocation. Fixes #406 in 5 lines of per-transport code and removes the entire class of "retry forever on permanently-broken factory" issues.

### S8 — End-to-end consumed-position backpressure

**Kills RC8 (1 issue + latent class).**

`MaybeSendAck` reports `_consumedPos` from the ReadChannel consumer. The wire-received position becomes purely diagnostic. Sender's credit window stalls when consumer is slow → producer's `WriteAsync` blocks on backpressure → documented contract holds. Also closes the entire latent class of "what if consumer is slow" bugs that the bug-finder hasn't found yet but will.

---

## Migration sequencing (smallest-blast-radius first)

Recommended order. Each step is independently shippable and reduces the open-issue surface measurably:

| Step | Change | Open issues closed | Risk |
|---|---|---|---|
| 1 | S1 — Non-throwing control queue | 9 | Low (additive API + delete throwing path) |
| 2 | S7 — Typed transport fault | 3 | Low (per-transport contract change) |
| 3 | S8 — Consumed-position ACK | 1 + latent | Low (one file, well-localized) |
| 4 | S3 — Transit event coalescer | 6 | Medium (touches 4 transits) |
| 5 | S2 — MuxState state machine | 12 | Medium-High (touches most of `StreamMultiplexer`) |
| 6 | S5 — Registry-owned channel lifecycle | 4 + #237 cleanup | Medium-High (data structure overhaul) |
| 7 | S4 — Phased Dispose with barrier | 7 | Medium (refactor, but isolated) |
| 8 | S6 — Cancellation taxonomy | 3 + latent | High (cross-cutting concern) |

**Project context allows breaking changes.** Take it. Public API surface should change wherever it improves safety — that's the cheapest version of this work.

---

## Root-cause → issue cross-reference

| RC | Structural fix | Issues |
|---|---|---|
| RC1 | S1 | #291, #336, #355, #365, #373, #374, #377, #392, #404 |
| RC2 | S2 | #370, #371, #378, #381, #382, #383, #391, #395, #396, #400, #401, #405 |
| RC3 | S3 | #370, #371, #381, #382, #396, #405 |
| RC4 | S4 | #368, #376, #384, #390, #397, #398, #403 |
| RC5 | S5 | #237 (cleanup), #367, #385, #399 |
| RC6 | S6 | #375, #380, #388 |
| RC7 | S7 | #386, #402, #406 |
| RC8 | S8 | #394 |

Total distinct issues addressed: **41 of 41 currently open**.

---

## What the bug-finder will look like after

Once these 8 changes land:

- The "Y also uses throwing WriteRawFrame" issue shape **cannot exist** — the throwing path is deleted.
- The "Y's latch not reset" issue shape **cannot exist** — there are no per-component latches.
- The "Y leaks resources on dispose" issue shape **cannot exist** — phase barrier enforces ordering, `DisposeGuard` catches single violations in DEBUG.
- The "transit fires event twice" issue shape **cannot exist** — coalescer is the only emitter.
- The "channel state race" issue shape mostly disappears — registry is atomic.

The bug-finder will surface genuinely new behaviors (e.g., protocol-level edge cases, transport-specific quirks, performance pathologies). That's the signal it should be producing.

---

## Non-goals

- **Not** a rewrite from scratch. `old/v3/v2/v1` are already archived; v4 is salvageable.
- **Not** adding more pragmas, `try/catch`, or defensive nullable annotations. Those are the current strategy and they are not working.
- **Not** documenting "be careful to reset the latch" as a convention. Conventions don't survive a bug-finder agent at scale; structure does.
