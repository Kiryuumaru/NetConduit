# Plan 065: Size-Gated Per-Frame Commit + Drain-Only for Large Frames

## What

Gate the per-frame commit optimization behind a size threshold (payload >= 8192 bytes) so it ONLY applies to bulk data frames. Add a drain-only method that skips `_writeLock` entirely. Game-tick paths (small messages) remain completely unchanged.

## Key Insight

Plans 059, 060, 064 all showed significant throughput improvements from per-frame commit (1ch×100KB: 0.24x → 0.57x in Plan 064) but ALL failed 1000ch game-tick with TimeoutException. Those plans applied CommitPipeWriter to EVERY write including 73-byte game-tick frames. While CommitPipeWriter overhead is small (~35-65ns), at 1000ch×64B with ~829K msg/s on 2-core pinned system, any additional per-write overhead contributes to instability.

By gating at >= 8192 bytes:
- Game-tick frames (73B for 64B msg, 265B for 256B msg) → **skip commit** → path identical to baseline
- Bulk frames (65545B for 64KB chunk) → **commit immediately** → TryDrainOnly proceeds without `_writeLock`

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs`: Conditional commit in SendFrameToWriter, new TryDrainOnlyAsync method
- `src/NetConduit/WriteChannel.cs`: Call TryDrainOnlyAsync instead of TryCommitAndDrainAsync for large frames

## Analysis

### Why per-frame commit helps throughput

In baseline, TryCommitAndDrainAsync requires `_writeLock` (to commit) then `_streamLock` (to drain). If FlushLoop holds `_writeLock` during its commit phase, TryCommitAndDrain's `Monitor.TryEnter` fails → drain skips entirely → data waits up to 1ms for next FlushLoop cycle.

With per-frame commit in SendFrameToWriter, data is committed inside the existing `_writeLock`. TryDrainOnlyAsync only needs `_streamLock`. FlushLoop holding `_writeLock` for commit no longer blocks the drain path. This reduces the interference window from `(_writeLock time + _streamLock time)` to just `(_streamLock time)`.

### Why the size gate protects game-tick

- 1000ch×64B: frame = 9 + 64 = 73 bytes. `payload.Length (64) < 8192` → no commit in SendFrameToWriter, FlushLoop-only path → **identical to baseline**
- 1000ch×256B: frame = 9 + 256 = 265 bytes. `payload.Length (256) < 8192` → **identical to baseline**
- Bulk 1ch×64KB: frame = 9 + 65536 = 65545 bytes. `payload.Length (65536) >= 8192` → commit + TryDrainOnly → **improved path**

### Expected throughput scenarios

The `toSend >= 8192` threshold in WriteChannel matches the commit gate:
- 1KB frames (toSend=1024): `1024 < 8192` → no commit, no TryDrainOnly → FlushLoop handles → **unchanged**
- 64KB frames (toSend=65536): `65536 >= 8192` → commit + TryDrainOnly → **improved**

Scenarios improved: 1ch×100KB, 1ch×1MB, 10ch×100KB, 10ch×1MB, 100ch×100KB, 100ch×1MB
Scenarios unchanged: all ×1KB variants, all game-tick

### References

- Plan 064 Run 1 throughput: 1ch×100KB 0.57x (baseline 0.24x), 10ch×1MB 0.91x (baseline 0.72x), 100ch×1MB 0.78x (baseline 0.55x)
- Plan 064 failed only on 1000ch×64B TimeoutException — all small-message scenarios
- Plans 059, 060 same pattern: throughput improved, 1000ch timeout

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md (especially 100KB/1MB scenarios)
- Game-tick ratios unchanged (1ch×64B > 10x, 50ch×64B >= 10.15x)
- 1000ch×64B and 1000ch×256B: no timeout (path unchanged)
- All 532 tests pass
