# [ARCH] `StreamMultiplexer` logic separation — methods, not just state

## Confidence
high (every method cited is verifiable on master; trade-off judgements are explicit)

## Severity
Medium

## Category
Cohesion / Single-Responsibility / Change-cost

## Relationship to prior critiques

This critique is a **follow-on to [`arch-002`](../arch-002-monolithic-streammultiplexer/README.md)**.

`arch-002` recommended *Hold* on extracting `MuxConnection`, then documented a 3-PR plan for **state consolidation** if adopted. The user has executed that plan:

| PR | Scope | Status |
|---|---|---|
| #167 | Extract `MuxConnection` field container (7 task/session fields) | **merged** |
| #174 | Move transport/loop-cts/control-channel/pending-pong into `MuxConnection` | **merged** |
| #184 | Pass `MuxConnection` as parameter to loop methods | draft open |

That work moved **state** off `StreamMultiplexer`. It did not move **logic**. On master, `StreamMultiplexer.cs` is **1,259 lines** (was 1,251 pre-arch-002 — slight growth from the `_conn.` trampoline). Every method enumerated in arch-002 still lives on the class.

`arch-002` was explicit that loop extraction was out of scope and that mesh-vs-no-mesh (`arch-001`) was the gating decision. This critique re-opens the question now that:
- the state extraction has shipped without regression (#167 and #174 are green on master),
- the user explicitly asked for a *systematic logic separation* review,
- mesh status (`arch-001`) is unchanged but the cost of touching this file has not improved.

## Current State

[`src/NetConduit/StreamMultiplexer.cs`](../../src/NetConduit/StreamMultiplexer.cs) on master is **1,259 lines**, single `sealed class` implementing `IStreamMultiplexer` + `IChannelOwner`. Method-level shape (line numbers verified on master after #174):

| Cluster | Members | Lines | Approx. size |
|---|---|---|---|
| Construction / validation | `Create`, `ValidateSlabSize`, `ValidateTimingOptions`, `EncodeValidatedChannelId` | 46–207 | ~150 |
| Public surface | `Start`, `WaitForReadyAsync`, properties, events | 209–222 + props | small |
| **Channel registration** | `OpenChannel`, `AcceptChannel`, `AcceptChannelsAsync`, `TryRegisterChannels`, `RollbackPartialBatch`, `PreparedRegistration` | 223–537 | **~315** |
| Channel lookup | `GetWriteChannel`, `GetReadChannel` | 538–542 | tiny |
| Session control | `GoAwayAsync`, `FlushAsync` | 544–584 | ~40 |
| **Lifecycle outer loop** | `MainLoopAsync` | 585–759 | **~175** |
| Lifecycle helpers | `AbortChannelsForTerminalTransportFailure`, `HasHandshakeRetryBudget` | 761–771 | small |
| Connect orchestration | `ConnectWithRetryAsync` | 773–834 | ~62 |
| Loop join | `WaitForLoopsAsync` | 836–848 | ~13 |
| `IChannelOwner` callbacks | `NotifyReady`, `NotifyChannelOpened`, `NotifyChannelCompleted`, `NotifyPendingAcceptCancelled` | 850–898 | ~50 |
| **Transport writer** | `RunWriterLoop`, `RunFlusherLoop` | 900–970 | ~70 |
| **Transport reader** | `RunReaderLoopAsync` | 972–1033 | ~62 |
| **Inbound dispatch** | `DispatchToChannel` | 1035–1130 | **~95** |
| Control-frame routing | `ProcessControlFrame`, `ProcessCtrlSubframe`, `HandleRemoteGoAway` | 1132–1173 | ~40 |
| **Keepalive** | `RunKeepaliveLoopAsync`, `WaitForPongAsync` | 1175–1225 | ~50 |
| Frame-build helpers | `SendControlFrame`, `SendInitAck`, `IChannelOwner.SendAck` | 1227–1262 | ~35 |
| **Handshake protocol** | `PerformHandshakeAsync`, `PerformReconnectHandshakeAsync`, `IsInitialHandshakeFrame`, `IsReconnectHandshakeFrame`, `ReadHandshakeFrameAsync`, `ReadExactAsync` | 1264–1405 | **~140** |
| Disposal | `DisposeAsync` | 1407–1452 | ~45 |
| Event raising | `RaiseEvent<T>`, `RaiseEvent` | 1454–end | tiny |

Bolded clusters are the candidates this critique examines.

## Why It Exists (Chesterton's Fence)

The reasons from arch-002 still apply — shared state across loops, reconnect-coordination cost, surgical-fix track record (#98 lifecycle, #104/#105 events+stats deferral, #121 events+uptime, #124 goaway drain, #127 frame validation, #130 ready ordering, #131 writechannel + peer ACK). Two new observations from inspecting the current master:

1. **State extraction did not auto-create logical seams.** `MuxConnection` is a passive bag. Every method that uses it still lives on `StreamMultiplexer` and reaches through `_conn.X` to get its fields. The file got slightly *longer* (1,259 vs 1,251) because of the indirection. State consolidation did not by itself improve method cohesion.
2. **The 5 surviving `volatile bool` flags (`_isRunning`, `_isConnected`, `_isReady`, `_isShuttingDown`, `_disconnectedFired`) are read-write across `MainLoopAsync`, `DisposeAsync`, `HandleRemoteGoAway`, and `GoAwayAsync`.** Logic separation that does not address these flags will leak ownership across the new boundaries.

## Concerns

### C1. `MainLoopAsync` (lines 585–759, ~175 lines) is a five-phase state machine written as straight-line code.

Reading the body, one phase blends into the next:

1. **Connect** (`ConnectWithRetryAsync`) — lines 596
2. **Handshake** (initial vs reconnect, with its own retry budget + delay loop nested inside the outer `while`) — lines 602–644
3. **Session-up** (control-channel lazy init, loop-CTS create, spawn writer/flusher/reader/keepalive tasks, raise `Connected`, raise `Ready` on first connect, `MarkConnected` channels) — lines 646–706
4. **Wait-for-fault** (`Task.WhenAny`, cancel CTS, `MarkDisconnected` channels, `WaitForLoopsAsync`, dispose loop CTS) — lines 708–732
5. **Teardown decision** (dispose transport, decide whether shutdown is `GoAwayReceived` / `TransportError` / dispose-driven, fire `Disconnected`) — lines 734–759

Every previously-fixed lifecycle bug (#98, #124, #130, #131) lived in transitions *between* these phases. The phase boundaries are not named; they are encoded only as positional comments. Adding a sixth case (e.g. mesh's "rejoin after partition") means re-reading all five phases to find the right insertion point.

### C2. `DispatchToChannel` (lines 1035–1130, ~95 lines) is two unrelated functions in a switch.

Branch `header.Flags == FrameFlags.Init` (lines 1037–1110, ~73 lines) does:
- channel-name length validation,
- UTF-8 decode,
- acquire `_registry.AcceptLock`,
- check existing / pending-accept-disposed / pending-accept-live / new,
- register read channel,
- mark open + connected,
- send init-ack,
- update stats,
- raise `ChannelAccepted`,
- enqueue for `AcceptChannelsAsync`.

That is **channel acceptance**, a registration-side concern.

The remainder (lines 1112–1130, ~18 lines) routes DATA/ACK/FIN/ERR to an already-registered channel. That is **dispatch**, a reader-loop concern.

They share zero data. They share the method name only because both are triggered by the reader thread reading a frame whose `ChannelIndex != ControlChannel`.

### C3. Handshake (lines 1264–1405, ~140 lines) is the most cleanly extractable cluster in the file.

`PerformHandshakeAsync` + `PerformReconnectHandshakeAsync` + 4 helpers form a self-contained protocol module:
- Inputs: `IStreamPair transport`, `Guid sessionId`, `Guid? expectedRemoteSessionId`, `CancellationToken ct`.
- Outputs: `Guid remoteSessionId`, `bool useOddIndices`.
- Side effects on `StreamMultiplexer`: writes `_conn.RemoteSessionId`, calls `_registry.SetIndexParity(bool)`.

No event raising. No volatile flag access. No registry mutation beyond the index-parity bit. Two of the six members are already `private static`. The other four take all their dynamic input from parameters except `_conn.SessionId` (read once) and `_conn.RemoteSessionId` (written once).

This cluster could be moved to `Internal/MuxHandshake.cs` with no behavior change and minimal API surface (a single `RunAsync` method per handshake form).

### C4. Frame-build helpers (lines 1227–1262, ~35 lines) duplicate the same 5-line byte-buffer pattern three times.

`SendControlFrame`, `SendInitAck`, and `IChannelOwner.SendAck` each:
1. Null-check `_conn.ControlChannel`.
2. Allocate `new byte[FrameHeader.Size + payloadLen]`.
3. Call `FrameHeader.WriteTo(...)`.
4. Copy / write payload.
5. Call `_conn.ControlChannel.WriteRawFrame(temp)`.

Three near-identical bodies. Each per-frame allocation also pressures the GC, which matters because `SendInitAck` and `IChannelOwner.SendAck` are on the per-frame hot path.

### C5. Keepalive is action-at-a-distance through `_conn.PendingPong`.

`RunKeepaliveLoopAsync` (1175) creates a `TaskCompletionSource`, stores it via `Interlocked.Exchange(ref _conn.PendingPong, pendingPong)`, sends Ping, then calls `WaitForPongAsync` which awaits the TCS. The TCS is *completed* in `ProcessControlFrame` (1141: `Interlocked.Exchange(ref _conn.PendingPong, null)?.TrySetResult()`), which is called from the reader thread.

The producer (reader) and consumer (keepalive) communicate only through a field on `MuxConnection`. This is correct and necessary today, but it means the keepalive subsystem cannot be reasoned about without also reading the reader-loop's control-frame switch — a textbook coupling-through-shared-state pattern.

### C6. Transport writer/flusher pair share knowledge that the rest of the class does not need.

`RunWriterLoop` (900) and `RunFlusherLoop` (947) both start with `var transport = _conn.Transport ?? throw ...; var writeStream = transport.WriteStream;` and both depend on `_readySignal` / `_flushSignal`. They are the *only* readers of `_readyChannels` (writer) and the *only* callers of `writeStream.Flush()` (flusher). No other code in the class touches `writeStream` for writing data frames.

### C7. `TryRegisterChannels` is ~200 lines but already internally cohesive.

Lines 309–514. Validates a batch, prepares index assignments, attempts atomic registration, rolls back on partial failure via `RollbackPartialBatch`. It is large, but its responsibility is single: "atomically register N channels or none". It is not a logic-separation candidate on cohesion grounds; it is only a candidate on raw size.

## Adversarial Questions Asked

- *"If we extracted handshake (C3), what regresses?"* — Any test that exercises GoAway during handshake (none observed in the integration test list), any test that uses a custom `IStreamPair` whose stream behavior diverges between handshake and post-handshake (currently unlikely because handshake uses async I/O and the steady-state writer uses sync I/O — extraction would preserve this asymmetry). Risk: low.
- *"If we extracted Keepalive (C5), what regresses?"* — Tests for ping timeout (#98, #131 history). The risk is that the `_conn.PendingPong` field becomes a public field on a `MuxKeepalive` object and the reader loop now reaches into that object instead of into `_conn`. Net coupling unchanged; surface visibility *increased*. The right time to do C5 is *after* the volatile-flag cleanup (deferred from #184) — otherwise the new object owns a TCS but the class still owns the "are we shutting down" answer it depends on.
- *"If we split `DispatchToChannel` (C2), is anything subtle in the locking?"* — The lock `_registry.AcceptLock` is acquired only in the INIT branch. Splitting INIT out into a `HandleInitFrame(header, payload)` helper preserves the lock acquisition site and changes nothing about its scope. Risk: very low.
- *"Why not just `partial class`?"* — Because the file is not the problem. The shared-mutable coupling is the problem. `partial class` makes the file shorter while keeping every field accessible from every method — a false signal.
- *"Will any of this survive the mesh decision (`arch-001`)?"* — C3 (handshake) and C4 (frame-build helpers) are mesh-neutral: mesh adds a routing layer above this, not inside it. C1 (MainLoopAsync state machine) is mesh-*sensitive* — mesh may add a phase between handshake and session-up.

## Alternatives Considered

### Option A: Do nothing
- **Benefits:** Zero regression risk. PR-3 (#184) still in flight; further refactor pressure is real burden on the maintainer. Preserves the recently-validated post-#174 state. If mesh is cancelled, the size becomes a fixed cost the project lives with.
- **Costs:** File grows with each feature. Cohesion problems (C1, C2, C5) compound. Future bug fixes in `MainLoopAsync` continue to require reading all five phases.
- **When this is right:** If the project's near-term roadmap touches `StreamMultiplexer.cs` rarely (e.g. only event-shape adjustments), and mesh remains undecided.

### Option B: Extract pure-helper modules only (C3 + C4)
- **Scope:** Move handshake (C3) to `Internal/MuxHandshake.cs`. Refactor frame-build helpers (C4) into a single `Internal/ControlFrameBuilder` static helper, eliminating the triplicated byte-buffer pattern.
- **Behavior change:** None. Pure code motion + one DRY fold.
- **Benefits:** Removes ~170 lines from `StreamMultiplexer.cs` without touching loops, lifecycle, or shared mutable state. Zero new types in concurrent paths. Handshake becomes independently testable for the first time. Frame-build helper can be unit-tested with a synthetic `WriteRawFrame` capture.
- **Costs:** Two new internal files. Minor parameter-passing overhead (negligible at handshake cadence).
- **Risks:** Very low. Handshake helpers are already nearly-pure; frame-build helpers are pure byte ceremony.
- **Reversibility:** Trivial (re-inline).

### Option C: Option B + split `DispatchToChannel` into `HandleInitFrame` + `DispatchExistingChannelFrame` (C2)
- **Scope:** Adds a method-local split inside `StreamMultiplexer`. No new file required (keep it as `private` methods on the class). Could move further into a separate `Internal/InboundFrameDispatcher` if a second consumer appears.
- **Behavior change:** None.
- **Benefits:** Names the two responsibilities. Reader loop's call site (line 1029) becomes a 2-way switch with explicit method names instead of a single 95-line method.
- **Costs:** Slightly more methods on the class.
- **Risks:** Low. Single-threaded call site; lock scope preserved.
- **Reversibility:** Trivial.

### Option D: Option C + extract `MuxKeepalive` + `MuxTransportWriter` as subsystem objects holding `MuxConnection` (C5 + C6)
- **Scope:** Create `Internal/MuxKeepalive.cs` owning `RunLoopAsync`, `WaitForPongAsync`, the pong TCS. Create `Internal/MuxTransportWriter.cs` owning `RunWriterLoop`, `RunFlusherLoop`. `StreamMultiplexer.MainLoopAsync` instantiates them, starts their tasks, awaits them in `WaitForLoopsAsync`.
- **Behavior change:** None intended; surface for *introducing* races increases.
- **Benefits:** Each subsystem becomes independently reviewable. The reader loop calls into `keepalive.OnPongReceived()` instead of mutating `_conn.PendingPong` directly — the action-at-a-distance becomes a named method call.
- **Costs:** Touches lifecycle code paths last hardened by #98/#124/#131. The pong TCS handoff between reader and keepalive must move atomically with the volatile-flag rules.
- **Risks:** Medium. Pre-requisite: the volatile-flag cleanup deferred from #184 should land first (or as part of this option), otherwise the new objects' methods will still read `_conn.IsShuttingDown`-equivalent state from a field they do not own.
- **Reversibility:** Medium. The subsystems are internal so consumers see nothing, but re-inlining is no longer trivial because lifecycle wiring is involved.

### Option E: Decompose `MainLoopAsync` into a `ConnectionStateMachine` (C1)
- **Scope:** Replace the 175-line outer-while-loop with a state-object cycling through `Connecting → Handshaking → SessionUp → Faulting → Teardown → (Reconnecting | Stopped)`. Explicit phase methods.
- **Behavior change:** None intended; the transitions must be byte-for-byte equivalent to the current straight-line code.
- **Benefits:** Names the phases. Bug fixes can target one phase. Mesh's hypothetical "rejoin after partition" gets an obvious insertion point.
- **Costs:** Highest. This is the part of the class whose bug-fix history is the longest (#98, #124, #130, #131). Any restructure here risks regressing those fixes. Cannot be done as a no-op refactor — the transitions become *more visible* and that visibility itself is what changes test assumptions.
- **Risks:** High. The integration test suite (10,878 lines) catches a lot but does not assert phase-level invariants.
- **Reversibility:** Low. Once phases are named in test names and error messages, reverting reintroduces a known-named structure as "back to monolith".

### Option F: `partial class` split, no logic change
- **Scope:** Move existing private methods into multiple `partial class StreamMultiplexer` files (`StreamMultiplexer.Handshake.cs`, `StreamMultiplexer.Reader.cs`, etc.).
- **Benefits:** Each file is shorter and one-topic. Diff-friendly.
- **Costs:** Does not change coupling. Every field on the partial is still accessible from every method. Reviewers may mistake the split for SRP enforcement when no enforcement exists.
- **Risks:** Cosmetic only.
- **Reversibility:** Trivial.

## Recommendation

**Adopt Option B as the next PR. Hold C, D, and E.**

Reasoning:

- **Option B is the only option whose cost is bounded and whose risk is empirically near zero.** Handshake and frame-build helpers have no shared mutable state with the loops, no volatile-flag interaction, and no lifecycle entanglement. Their extraction is the closest analogue to what `WriteChannel`/`ReadChannel` already did successfully for per-channel state — but on the protocol side rather than the data-plane side.
- **Option C (DispatchToChannel split) is independently safe** but its value is small (one named method) compared to its review cost (re-reading the reader loop). Defer until either a second consumer of accept logic appears (e.g. mesh) or the dispatch path needs to change for an unrelated reason. Note in the file as a known split candidate.
- **Option D (Keepalive + TransportWriter subsystem objects) is the right next step *after* the volatile-flag cleanup that PR-3 (#184) deferred.** Without that cleanup, the new objects would still read shutdown state through `_conn` reach-throughs — same coupling, more files. Sequencing: land #184, do the volatile-flag pass, then revisit D.
- **Option E (ConnectionStateMachine) should remain held until mesh is decided (`arch-001`).** This is the same logic arch-002 used for Option B; the reasoning carries forward to its method-level analogue. If mesh is cancelled, E becomes more attractive (the file is a stable monolith and worth restructuring). If mesh proceeds, E should be co-designed with mesh's "rejoin" phase rather than retrofitted afterward.
- **Option F (partial class) is rejected.** It signals structural improvement without delivering any.

## If Adopted: Handoff to Dev (Option B only)

**Files affected:**
- New: `src/NetConduit/Internal/MuxHandshake.cs` — holds the 6 handshake members currently on `StreamMultiplexer` (lines 1264–1405). Public surface: one entry per handshake form. Both take `IStreamPair`, `Guid localSessionId`, `CancellationToken` and return a small result record `(Guid RemoteSessionId, bool UseOddIndices)` for initial / `(Guid RemoteSessionId)` for reconnect (parity is already established by initial handshake).
- New: `src/NetConduit/Internal/ControlFrameBuilder.cs` — single static helper consolidating the byte ceremony from `SendControlFrame`, `SendInitAck`, and `IChannelOwner.SendAck` (current lines 1227–1262). Callers still own the `_conn.ControlChannel` null-check and the `WriteRawFrame` call; the helper only owns header+payload composition into a returned `byte[]`. This keeps the control-channel null-check + writer at the call site where it belongs.
- Modified: `src/NetConduit/StreamMultiplexer.cs` — replaces six handshake members with one call site per form; replaces three byte-build blocks with calls into the new builder.

**Behavior to preserve (must not change):**
- Handshake wire format (both initial and reconnect), including the cross-acceptance of "initial received during reconnect" and "reconnect received during initial" documented in the current comments at lines 1290–1298 and lines 1334–1345. These compatibility branches are non-obvious and must move into `MuxHandshake` *with* their comments.
- `HandshakeTransportException` semantics: thrown for I/O failure (retryable), `MultiplexerException(ProtocolError)` thrown for malformed frames (terminal), `MultiplexerException(SessionMismatch)` thrown for session-id mismatch on reconnect (terminal). These distinctions drive `MainLoopAsync`'s retry-budget logic and must round-trip exactly.
- Frame-build helper layout: `FrameHeader.Size + payloadLen` allocation, big-endian write through `BinaryPrimitives.WriteUInt64BigEndian` (line 1259), `FrameHeader.WriteTo` ordering. The returned `byte[]` must be byte-identical to what is sent today.

**Frozen API surface:**
- All members of `IStreamMultiplexer`.
- All public events on `StreamMultiplexer`.
- `IChannelOwner` (note: `SendAck` stays on `StreamMultiplexer` as the `IChannelOwner` member; it just delegates header/payload assembly to the new helper).
- `MultiplexerOptions`, `ChannelOptions`, `MultiplexerStats`, `ChannelStats`.

**Suggested PR slicing:**
1. PR-A (smaller): introduce `ControlFrameBuilder`, fold the three frame-build call sites onto it. ~50 lines diff. Independently shippable.
2. PR-B (larger): extract `MuxHandshake`. ~200 lines moved. Independently shippable.

Order is interchangeable; PR-A is the safer first step.

**Tests that must keep passing:**
- All of `tests/NetConduit.UnitTests` (10,878 lines), especially the reconnect / handshake / session-mismatch tests.
- All transport integration test suites — particularly the TCP / WebSocket / IPC reconnect and keepalive paths.

**Out of scope for this critique's recommendation:**
- Options C, D, E, F (covered above with explicit reasoning).
- Removing the 5 surviving `volatile bool` flags (deferred from #184; pre-requisite for Option D).
- `MultiplexerStats` mutability (`arch-004`).
- `TimeProvider` (`arch-005`).
- Logging (`arch-006`).

## References

- `src/NetConduit/StreamMultiplexer.cs` (lines 1–1259 on master) — subject of this critique.
- `src/NetConduit/Internal/MuxConnection.cs` — post-#174 state container.
- `investigate/arch-002-monolithic-streammultiplexer/README.md` — predecessor critique (state separation).
- `investigate/arch-001-mesh-branch-vs-stated-scope/README.md` — gating decision for Option E.
- PRs #167, #174 (merged), #184 (draft) — the executed state-separation plan from arch-002.
- Bug history bearing on lifecycle code: #98, #104, #105, #121, #124, #127, #130, #131.
