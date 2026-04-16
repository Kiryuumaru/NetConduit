# Plan 063: Reset _unflushedDataBytes in TryCommitAndDrain + FIN drain-on-close

## What

Two targeted changes:

1. **Reset `_unflushedDataBytes = 0` inside `TryCommitAndDrainAsync` under `_writeLock` after `CommitPipeWriter`** — makes the counter accurate (tracks bytes written since last commit, not since last ForceFlush/FlushLoop).

2. **Call `TryCommitAndDrainAsync` from `WriteChannel.CloseAsync` after `SendFin`** — drains the FIN frame (and any pending data) immediately on the caller thread instead of waiting for FlushLoop.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — `TryCommitAndDrainAsync`: add `_unflushedDataBytes = 0` inside `_writeLock` block
- `src/NetConduit/WriteChannel.cs` — `CloseAsync`: return `TryCommitAndDrainAsync` instead of `default`

## Analysis

### Problem: _unflushedDataBytes is inaccurate

`_unflushedDataBytes` is incremented in `SendFrameToWriter` for every frame written.
It is reset to 0 only by `ForceFlushPipeToStreamAsync` and `FlushLoopAsync`.
`TryCommitAndDrainAsync` commits and drains data but does NOT reset the counter.

For 1ch×1MB (16 frames of 64KB):
- Frame 1: unflushed = 65545. TryCommitAndDrain succeeds, drains 64KB. Counter stays 65545.
- Frame 2: unflushed = 131090. TryCommitAndDrain drains. Counter stays.
- Frame 3: unflushed = 196635. TryCommitAndDrain drains. Counter stays.
- Frame 4: unflushed = 262180 ≥ 262144 → **ForceFlush triggered** (blocking both locks).

ForceFlush is triggered even though the pipe only contains 1 frame (~64KB) — previous 3 were already drained by TryCommitAndDrain. The counter over-reports, causing unnecessary blocking lock acquisition.

Pattern repeats every 4 frames: 3 × TryCommitAndDrain + 1 × ForceFlush = 4 blocking ForceFlush per 16 frames.

### Fix: Reset counter after successful commit

After `CommitPipeWriter` inside TryCommitAndDrain's `_writeLock` section, set `_unflushedDataBytes = 0`. This is the same synchronization pattern used by FlushLoop and ForceFlush (all three reset under the same lock).

With the fix:
- Frame 1: unflushed = 65545 → TryCommitAndDrain → commit + reset to 0 → drain
- Frame 2: unflushed = 65545 → TryCommitAndDrain → commit + reset to 0 → drain
- All 16 frames: TryCommitAndDrain (non-blocking). ForceFlush never triggers.

### Why this differs from Plan 061 (FAILED)

Plan 061 used `Interlocked.Add(ref _unflushedDataBytes, -drained)` **after drain, outside the lock**. This raced with FlushLoop's `Interlocked.Exchange(ref _unflushedDataBytes, 0)`:
1. TryCommitAndDrain drains N bytes
2. FlushLoop: Exchange(0) → counter = 0
3. TryCommitAndDrain: Add(-N) → counter goes **negative** → ForceFlush never triggers

This plan uses `_unflushedDataBytes = 0` **inside `_writeLock`**, the same lock used by FlushLoop and ForceFlush for their resets. No race condition possible.

### FIN drain-on-close

Currently `CloseAsync` sends FIN + SignalFlush but returns immediately. The FIN waits in the pipe for FlushLoop (~100-200µs). Adding `TryCommitAndDrainAsync` drains the FIN immediately on the caller thread.

For game-tick: CloseAsync is called **after** `Stopwatch.Stop()` → zero impact.
For throughput: CloseAsync is inside the timed region → faster FIN delivery → reader sees EOF sooner.

### Expected impact

Single-channel throughput:
- ForceFlush no longer triggers (all drains via non-blocking TryCommitAndDrain)
- Eliminates 4 blocking ForceFlush calls per 1MB transfer
- Actual improvement: marginal (no contention for single-channel, both paths succeed immediately)

Multi-channel throughput:
- ForceFlush only triggers when locks are genuinely contended (counter accumulates because TryCommitAndDrain failed to acquire locks)
- Fewer blocking drain calls → better concurrency
- Expected: 5-15% improvement for 10ch, 100ch scenarios

Small transfers (1KB):
- FIN drain-on-close saves FlushLoop scheduling latency (~100-200µs)
- Expected: 10-30% improvement for 1ch×1KB

Game-tick: zero impact (frames < 8192 don't use TryCommitAndDrain; CloseAsync outside timer)

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md
- Game-tick 1ch×64B NC vs FRP > 10x
- Game-tick 50ch×64B within 5% of docs (10.68x → ≥10.15x)
- No 1000ch×64B timeout
- All tests pass
