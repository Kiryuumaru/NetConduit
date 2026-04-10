# Plan 017: Accumulation-Based Flush (No SignalFlush Fallback)

**Addresses:** Per-frame ForceFlush overhead (16 TCP writes for 1MB of 64KB chunks)

## Analysis

Plan 013 added ForceFlush per frame >= 65536 bytes. For 1MB (16 × 64KB), this means 16 separate TCP syscalls. Each takes ~30-50µs for loopback, totaling ~500-800µs of I/O overhead.

Plan 014 tried to batch using an accumulation threshold but used SignalFlush as a fallback for sub-threshold writes. SignalFlush woke the FlushLoop, which drained partial data, defeating the batching.

**Key insight:** The 16 WriteAsync(64KB) calls in the benchmark complete in ~16µs (pipe writes only). The 1ms FlushLoop timer won't fire during this window. If we simply DON'T flush or signal until the threshold is reached, all writes naturally batch.

## Change

1. Track `_unflushedDataBytes` in StreamMultiplexer (incremented under _writeLock, reset on drain)
2. WriteAsync: ForceFlush only when accumulated bytes >= 262144 (256KB), else do nothing
3. Before credit stall: flush any pending data so receiver can grant credits
4. FlushLoop 1ms timer serves as safety net for sub-threshold remainder

**Key difference from Plan 014:** No SignalFlush for sub-threshold writes. FlushLoop timer is the only fallback.

## Expected Behavior

| Scenario | Plan 013 | Plan 017 |
|----------|----------|----------|
| 1ch×1MB (16×64KB) | 16 TCP writes | 4 TCP writes (256KB each) |
| 10ch×1MB (concurrent) | ~160 TCP writes | ~40 TCP writes |
| Game-tick 64B | FlushLoop timer | FlushLoop timer (no change) |

## Success Criteria

- 1ch×1MB throughput improves (target: >800 MB/s)
- Multi-channel throughput improves
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%
