# Plan 013: Caller-Side Immediate Drain for Large Data Frames

**Addresses:** 1ms FlushLoop timer latency for bulk data writes (Problem 2 in PROBLEMS.md)

## Change

**Files:** `src/NetConduit/StreamMultiplexer.cs`, `src/NetConduit/WriteChannel.cs`

When `WriteChannel.WriteAsync` sends a large frame (payload >= 65536 bytes), immediately commit the PipeWriter and drain to the transport stream on the caller's thread. Small frames (game-tick) still use the FlushLoop timer path unchanged.

### Why This Differs from Plan 012

Plan 012 used `SignalFlush()` for large frames. This wakes the FlushLoop (a separate async task), which must be scheduled on a CPU core by the thread pool. On 2 cores with 6 active tasks, scheduling latency can be 10-100µs — negating the benefit.

Plan 013 bypasses the FlushLoop entirely. The caller thread (already running) does: write → commit → drain. Zero cross-thread scheduling overhead.

### Implementation

1. Make `ForceFlushPipeToStreamAsync` internal (currently private) — it already does exactly commit + drain
2. In `WriteChannel.WriteAsync`, after `SendDataFrame`, if the chunk was >= 65536 bytes, call `ForceFlushPipeToStreamAsync`

### Thread Safety

- `_writeLock` serializes commit; `_streamLock` serializes drain — same as FlushLoop
- Lock order is consistent (_writeLock → _streamLock) — no deadlock
- If FlushLoop is draining (holds _streamLock), caller waits briefly (correct)
- If caller is draining, FlushLoop finds nothing new (correct, just a no-op cycle)

## Success Criteria

- Bulk throughput improves toward 2x baseline (or near Yamux/Smux)
- Game-tick 1ch×64B stays above 1,200,000 msg/s (baseline: 1,417,077)
- Game-tick 50ch×64B does not regress more than 5% (baseline: 951,194)
