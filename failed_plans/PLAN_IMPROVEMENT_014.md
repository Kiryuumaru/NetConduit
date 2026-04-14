# Plan 014: Accumulation-Based Flush Threshold

**Addresses:** Per-frame ForceFlush overhead from Plan 013 — 16 TCP writes for 1MB instead of batching

## Analysis

The benchmark sends 16 × 64KB WriteAsync calls for 1ch×1MB. Plan 013 does ForceFlush per frame (16 TCP writes). Yamux/Smux batch frames into fewer TCP writes. Reducing TCP write count should improve throughput.

## Change

**Files:** `src/NetConduit/StreamMultiplexer.cs`, `src/NetConduit/WriteChannel.cs`

1. Add `_unflushedDataBytes` counter to StreamMultiplexer (incremented under `_writeLock`, reset on drain)
2. In WriteChannel.WriteAsync: ForceFlush only when accumulated `_unflushedDataBytes >= 262144` (256KB). Otherwise SignalFlush to wake FlushLoop.
3. Reset counter in both ForceFlush and FlushLoop drain paths.

### Expected behavior for 1ch×1MB (16 × 64KB):
- Writes 1-3: accumulate 192KB → SignalFlush (FlushLoop may batch)
- Write 4: 256KB threshold → ForceFlush (drains 4 frames in 1 TCP write)
- Total: ~4 ForceFlush calls vs 16 (Plan 013)

### Game-tick 64B: No change (< 65536 threshold, neither path triggers)

## Success Criteria

- Bulk throughput improves beyond Plan 013 baseline
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%
