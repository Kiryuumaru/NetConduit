# Plan 067: Larger Pipe Segments + SpinWait Retry in TryCommitAndDrain

## What

Two orthogonal, low-risk changes:

1. **Increase Pipe `minimumSegmentSize` from 4096 (default) to 65536** — Reduces drain syscalls by keeping more data in fewer, larger segments. Directly helps game-tick 1000ch stability (fewer WriteAsync calls per FlushLoop cycle).

2. **Add SpinWait retry in TryCommitAndDrainAsync** — When `Monitor.TryEnter(_writeLock)` fails (FlushLoop holds it during Phase 1), spin briefly (~50-100ns) then retry. FlushLoop's Phase 1 is ~35-65ns, so the retry usually succeeds. Makes commit more reliable for throughput writes without adding blocking to game-tick (which never calls this method).

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs`: Add `minimumSegmentSize: 65536` to Pipe creation; add SpinWait retry in TryCommitAndDrainAsync

## Analysis

### Why larger segments help

Default minimumSegmentSize is 4096 (4KB). For 1000ch game-tick (~73K bytes per FlushLoop cycle):

| Metric | 4KB segments | 65KB segments |
|--------|-------------|---------------|
| Segments per cycle | ~18 | ~2 |
| WriteAsync calls in drain | ~18 | ~2 |
| Syscall reduction | baseline | **9x fewer** |

For throughput, fewer segments means `buffer.IsSingleSegment` is more likely true, avoiding the multi-segment iteration entirely in `WriteBufferToStreamAsync`.

### Why SpinWait helps throughput

TryCommitAndDrainAsync uses `Monitor.TryEnter(_writeLock)`. If FlushLoop holds _writeLock during Phase 1 (~35-65ns), TryEnter fails and the method returns WITHOUT committing. Data waits up to 1ms for FlushLoop.

With SpinWait(20) (~50-100ns): overlaps with FlushLoop's Phase 1. The retry TryEnter succeeds. Data gets committed + drained immediately.

| Scenario | TryEnter only | TryEnter + SpinWait |
|----------|--------------|---------------------|
| FlushLoop in Phase 1 | SKIP (wait 1ms) | Retry after ~100ns → succeed |
| Another channel in SendFrameToWriter | SKIP | SKIP (spin too short for ~10µs) |
| Lock free | succeed | succeed (no spin) |

### Game-tick safety

- Game-tick `toSend` values: 64 (64B msg) or 256 (256B msg)
- Both are `< 8192` → TryCommitAndDrainAsync is NEVER called → SpinWait never executes
- minimumSegmentSize: no behavioral change to any method, only Pipe internal allocation

### Why not remove the accumulation gate (Plan 066 lesson)

Plan 066 removed `unflushed >= 262144` from ForceFlush. This caused every 64KB frame to get immediate ForceFlush → 16 TCP writes of 65KB per 1MB instead of 4 writes of 262KB. Result: 1ch×1MB REGRESSED from 0.74x to 0.38x. Batching at 262KB is valuable for large transfers.

### Expected improvements

| Scenario | Change | Mechanism |
|----------|--------|-----------|
| Game-tick 1000ch | Positive | 9x fewer drain syscalls → faster FlushLoop → more stable |
| Throughput ×100KB | Positive | TryCommitAndDrain succeeds more often → less FlushLoop wait |
| Throughput ×1MB | Neutral-positive | Commit more reliable, batching preserved |
| Throughput ×1KB | Unchanged | Below TryCommitAndDrain threshold |

### References

- Plan 066: removing accumulation gate HURT 1MB throughput (lost batching)
- Plan 065: size-gated commit helped 2/3 runs but 1000ch marginal under system pressure
- Plans 059/060: used 65KB segments (combined with per-frame commit, failed on 1000ch)

## Success Criteria

- Bulk throughput ratios improved or maintained vs docs/benchmarks.md
- Game-tick ratios maintained or improved (fewer drain syscalls should help)
- 1000ch×64B: no timeout
- All 532 tests pass
