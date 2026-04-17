# Plan 066: Larger Pipe Segments + Remove ForceFlush Accumulation Gate

## What

Two orthogonal changes to the write/drain pipeline:

1. **Increase Pipe `minimumSegmentSize` from 4096 (default) to 65536** — Reduces the number of `WriteAsync` syscalls in `WriteBufferToStreamAsync` by keeping more data in fewer, larger segments.

2. **Remove the accumulation gate from ForceFlush** — Change `toSend >= 65536 && unflushed >= 262144` to just `toSend >= 65536`. Every large frame (64KB chunk) gets guaranteed immediate commit + drain instead of probabilistic TryCommitAndDrain.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs`: Add `minimumSegmentSize: 65536` to Pipe creation
- `src/NetConduit/WriteChannel.cs`: Simplify ForceFlush condition

## Analysis

### Why larger segments help

Default `minimumSegmentSize` is 4096 (4KB). For 1000ch game-tick:

- Current: 1000 frames × 73 bytes = 73,000 bytes. At 4KB segments: ~18 segments per FlushLoop cycle → `WriteBufferToStreamAsync` iterates with 18 `writeStream.WriteAsync` calls (18 syscalls).
- Proposed (65KB segments): 73,000 bytes fits in ~2 segments → 2 syscalls. **9x fewer syscalls per drain.**

For throughput with 64KB frames:
- Current: each `GetSpan(65545)` allocates one 65536+ segment. After 4 frames (ForceFlush): 4 segments → 4 syscalls.
- Proposed: same segment behavior for individual frames, but intermediate drains use fewer segments when data hasn't accumulated as much.

### Why removing the accumulation gate helps throughput

Current flow for 64KB frame writes:
- Frame 1: `toSend=65536 >= 65536`, but `unflushed=65545 < 262144` → falls to `TryCommitAndDrainAsync` (non-blocking, may SKIP if locks held)
- Frame 2: `unflushed=131090 < 262144` → TryCommitAndDrain again (may skip)
- Frame 3: `unflushed=196635 < 262144` → TryCommitAndDrain again (may skip)
- Frame 4: `unflushed=262180 >= 262144` → ForceFlush (guaranteed)

If TryCommitAndDrain skips (FlushLoop holds `_writeLock`), frames 1-3 wait up to 1ms for FlushLoop. This adds latency to every 3-out-of-4 large writes.

Proposed flow:
- Every frame: `toSend=65536 >= 65536` → ForceFlush → guaranteed commit + drain

ForceFlush commit lock hold time: ~35-65ns (trivial). Then releases `_writeLock` before drain. Other channels can write during drain.

### Game-tick safety

- Game-tick `toSend` values: 64 (for 64B msg) or 256 (for 256B msg)
- Both are `< 65536` → NEVER enter ForceFlush branch → path unchanged
- Both are `< 8192` → NEVER enter TryCommitAndDrain branch → FlushLoop-only → unchanged
- `minimumSegmentSize` change: no effect on any method behavior, only on Pipe's internal buffer allocation → zero logical impact on game-tick

### Multi-channel throughput concern

For 100ch × 1MB: 100 channels all calling ForceFlush per frame → blocking `lock(_writeLock)`. Lock hold time ~35-65ns → queue of 100 = ~6.5µs worst case. Drain under `_streamLock` serializes TCP writes (necessary regardless). Net effect: guaranteed drains outweigh minor lock queuing.

### Expected improvements

| Scenario | Change | Mechanism |
|----------|--------|-----------|
| All game-tick | Positive | Fewer drain syscalls (18→2 for 1000ch) |
| Throughput ×100KB, ×1MB | Positive | Guaranteed ForceFlush per large frame |
| Throughput ×1KB | Unchanged | Below both thresholds, FlushLoop-only |

### References

- Plans 059, 060, 064, 065: per-frame commit improved throughput but all failed 1000ch game-tick
- Plan 053 (succeeded): removed upper bound from TryCommitAndDrain → simplified conditions
- Plan 062 (failed): lowered TryCommitAndDrain threshold to 1024 → neutral/regressed

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md (especially 100KB, 1MB scenarios)
- Game-tick ratios unchanged or improved (fewer drain syscalls should help stability)
- 1000ch×64B and 1000ch×256B: no timeout
- All 532 tests pass
