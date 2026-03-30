# Performance Hardening Plan

Concrete changes to improve **both** bulk throughput **and** small-message rate.
All changes are internal to the write pipeline — public API surface unchanged, all features preserved.

## Goal

Close the throughput gap with Go multiplexers (Yamux, Smux) while maintaining or extending
the small-message advantage. Target: competitive on bulk, dominant on small messages.

| Workload | Current vs Go | Target |
|----------|--------------|--------|
| Bulk throughput | 0.4-0.8x | 0.8-1.2x |
| Small messages | 1.7-2.6x | 2.0-3.0x |

---

## 1. Queue-Based Write Coalescing

**Bulk impact:** +50-100% — channels never block each other, writer batches frames
**Small msg impact:** +20-40% — natural coalescing, dozens of frames per syscall

Replace direct `_writeLock` acquisition per frame with a single dedicated writer task draining a priority queue.

### Current

```
Channel A → _writeLock → WriteAsync → unlock
Channel B → _writeLock → WriteAsync → unlock  (blocked while A writes)
Channel C → _writeLock → WriteAsync → unlock  (blocked while A or B writes)
```

### Proposed

```
Channel A → enqueue (lock-free)
Channel B → enqueue (lock-free)
Channel C → enqueue (lock-free)
Writer task → dequeue batch → combined write → flush
```

### Changes

- `SendFrameOptimizedAsync` enqueues to `_sendQueue` instead of acquiring `_writeLock`
- New `WriteLoopAsync` task drains queue, batches frames, writes to transport
- `FlushLoopAsync` removed — writer flushes after each drain cycle
- Priority ordering preserved — `PriorityQueue` already exists at line 42

### Files

- `src/NetConduit/StreamMultiplexer.cs`

---

## 2. PipeWriter Transport Writer

**Bulk impact:** +20-30% — eliminates two-write problem, zero-copy frame assembly
**Small msg impact:** +10-20% — avoids ArrayPool rent/return per frame, fewer syscalls

Replace `Stream.WriteAsync` with `System.IO.Pipelines.PipeWriter` for the write side.

### Current

- Small frames: `ArrayPool` rent → copy header + payload → `WriteAsync` → return
- Large frames: two `WriteAsync` calls (header, payload) under lock

### Proposed

- All frames: `GetSpan` → write header + payload directly → `Advance` → `FlushAsync` after batch
- Zero-copy frame assembly, no `ArrayPool` for writes
- Eliminates two-write problem for large frames

### Changes

- Writer task from step 1 uses `PipeWriter` instead of raw `Stream`
- `IStreamPair` gains optional `PipeWriter WriteOutput` property
- Transport implementations provide `Pipe`-wrapped streams

### Files

- `src/NetConduit/StreamMultiplexer.cs`
- `src/NetConduit/IStreamPair.cs`
- `src/NetConduit/StreamPair.cs`

### Dependency

- Step 1 (queue-based writer)

---

## 3. Incremental Credit Grants

**Bulk impact:** +30-50% — sender stays busy longer, rarely hits zero credits
**Small msg impact:** +5-10% — grants still happen faster even though stalls are rare

Grant credits back to senders sooner to reduce stop-and-wait stalls.

### Current

- Receiver waits until 50% of window consumed, then grants all at once
- With 4MB window: sender sends 4MB → stalls → receiver reads 2MB → grant → sender resumes

### Proposed

- Grant when consumed >= `max(16KB, window / 4)`
- With 4MB window: grants at 1MB consumed — sender rarely hits zero

### Changes

```csharp
// AdaptiveFlowControl.RecordConsumptionAndGetGrant
var minGrant = Math.Max(16 * 1024, _currentWindowSize / 4);
if (_bytesConsumedInWindow >= minGrant)
{
    var toGrant = (uint)_bytesConsumedInWindow;
    _bytesConsumedInWindow = 0;
    return toGrant;
}
```

### Files

- `src/NetConduit/Internal/AdaptiveFlowControl.cs`

---

## 4. Zero-Copy Reconnection Buffer

**Bulk impact:** +10-15% — removes allocation + copy per chunk
**Small msg impact:** +5% — small allocs are cheap but still add up at 160K msg/s

Eliminate per-chunk `.ToArray()` allocation when reconnection is enabled.

### Current

```csharp
_syncState.RecordSend(rented.AsMemory(0, toSend).ToArray());  // alloc per chunk
```

### Proposed

- `RecordSend` accepts `ReadOnlySpan<byte>` and copies into pre-allocated ring buffer
- Ring buffer sized to `ReconnectBufferSize` — same memory budget, zero per-send allocation

### Files

- `src/NetConduit/WriteChannel.cs`
- `src/NetConduit/Internal/ChannelSyncState.cs` (or wherever `RecordSend` lives)

### Dependency

- None (independent of steps 1-3)

---

## 5. Reconnect Bypass Optimization

**Bulk impact:** +10-15% when reconnection disabled — eliminates branch + rent per chunk
**Small msg impact:** +5% when reconnection disabled — streamlined hot path

Skip `ArrayPool` rent/copy entirely when `EnableReconnection = false`.

### Current

The non-reconnect path already skips `RecordSend` but the code structure still enters
the reconnection branch check per chunk.

### Proposed

- Hoist the `EnableReconnection` check outside the chunking loop
- Use a direct send delegate to eliminate branch per chunk

### Files

- `src/NetConduit/WriteChannel.cs`

---

## Execution Order

```
Step 1: Queue-based writer       ← highest value, enables step 2
Step 3: Incremental credit grants ← independent, low complexity
Step 4: Zero-copy reconnect buffer ← independent, low complexity
Step 5: Reconnect bypass          ← trivial
Step 2: PipeWriter                ← builds on step 1
```

After each step: `dotnet build` (0 warnings) → `dotnet test` (100% pass) → benchmark comparison.

---

## Impact Summary

| # | Change | Bulk | Small msgs | Complexity |
|---|--------|------|-----------|------------|
| 1 | Queue-based writer | +50-100% | +20-40% | Medium |
| 2 | PipeWriter | +20-30% | +10-20% | Medium |
| 3 | Incremental credit grants | +30-50% | +5-10% | Low |
| 4 | Zero-copy reconnect buffer | +10-15% | +5% | Low |
| 5 | Reconnect bypass | +10-15% | +5% | Trivial |

---

## Test Plan

277 existing tests across 6 projects validate every feature that must survive.
Every step must pass all tests before proceeding to the next.

### Test Command Per Step

```
dotnet build                                    # 0 warnings, 0 errors
dotnet test tests/NetConduit.UnitTests          # 248 tests — core correctness
dotnet test tests/NetConduit.Tcp.IntegrationTests   # 8 tests — real TCP
dotnet test                                     # all 277 tests — full suite
```

### Critical Tests Per Step

#### Step 1 (Queue-Based Writer) — touches frame sending, lock structure

Must pass:
- `BasicMultiplexerTests` (7) — handshake, open, accept, send, large data, close, stats
- `ConcurrencyTests` (4) — multi-channel, bidirectional, multi-writer, rapid open/close
- `BackpressureTests` (4) — slow reader blocks, timeout, auto-grant, infinite timeout
- `PriorityTests` (3) — multi-channel priority, all levels, custom value
- `DataIntegrityTests` (6) — checksum verification, high channel count, index reuse
- `DataIntegrityStressTests` (5) — 10K channels, heavy load checksum
- `ChaosRobustnessTests` (22) — up to 1M channels, random operations, SHA256 checksums
- `ExtremeTests` (33+) — nested mux, 1GB transfer, 100K channels, chaos
- `NegativeTests` (18) — disconnect during write/read, concurrent open/close
- `DisconnectionTests` (30) — GoAway, transport error, channel close ordering
- `PerformanceTests` (5) — throughput, latency, message rate (must not regress)
- `TransitTests` (38) — message transit, stream transit, concurrent sends
- All integration tests (25) — TCP, WebSocket, UDP, IPC, QUIC

#### Step 3 (Incremental Credit Grants) — touches AdaptiveFlowControl

Must pass:
- `BackpressureTests` (4) — credit grant behavior changes
- `MemoryPressureTests` (9) — credit tracking, buffer limits, backpressure stats
- `DataIntegrityTests` (6) — data still arrives correctly with different grant timing
- `PerformanceTests` (5) — throughput must improve or hold steady

#### Step 4 (Zero-Copy Reconnect Buffer) — touches ChannelSyncState

Must pass:
- `ReconnectionTests` (22) — sync state tracking, buffer eviction, data replay
- `StreamFactoryTests` (14) — auto-reconnect, data continuity after reconnect
- `MemoryPressureTests` (9) — buffer limits, eviction, zero-length buffer

#### Step 5 (Reconnect Bypass) — touches WriteChannel hot path

Must pass:
- `BasicMultiplexerTests` (7) — data still flows
- `ReconnectionTests` (22) — reconnection still works when enabled
- `MemoryPressureTests.DisabledReconnection_NoBuffering` — bypass confirmed

#### Step 2 (PipeWriter) — touches transport layer

Must pass:
- All 277 tests — this changes the transport write layer, everything must pass
- All integration tests especially — real TCP/WebSocket/UDP/IPC/QUIC transports

### Test Failure Response

- If any test fails after a step: **full revert of that step**, investigate root cause
- Never apply the next step on top of a failing step
- Never comment out or skip failing tests

---

## Verification

- All existing unit tests pass (248 in NetConduit.UnitTests)
- All integration tests pass (TCP 8, WebSocket 9, UDP 4, IPC 4, QUIC 4)
- `dotnet build` produces 0 warnings, 0 errors
- Re-run Docker benchmarks after all steps to measure actual improvement
- Compare against baseline numbers in `benchmarks/docker/results/`

---

## Non-Goals

- No public API changes
- No feature removal
- No new configuration options required (internal tuning only)
- No breaking changes to wire protocol
