# Plan 064: Per-Frame Commit in SendFrameToWriter

## What

Two changes:

1. **Per-frame CommitPipeWriter** — Call `CommitPipeWriter(writer)` inside `SendFrameToWriter` after `writer.Advance()`, under the existing `_writeLock`. Data becomes visible to PipeReader immediately.

2. **Simplify TryCommitAndDrainAsync** — Remove the `_writeLock` commit section. Since data is committed per-frame, `TryCommitAndDrainAsync` only needs to drain (acquire `_streamLock`, TryRead, write to stream).

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — Per-frame commit in `SendFrameToWriter`, simplify `TryCommitAndDrainAsync`

## Analysis

### Why this helps multi-channel bulk throughput

**Current bottleneck**: When multiple channels write concurrently, `TryCommitAndDrainAsync` acquires `_writeLock` (to commit) then `_streamLock` (to drain). While draining (~100µs TCP write), _writeLock is free but `_streamLock` is held. Other channels' drain attempts fail at `_streamLock.Wait(0)` — they write and commit, but data waits for FlushLoop's 1ms timer before being drained.

With per-frame commit, `TryCommitAndDrainAsync` only needs `_streamLock`. This eliminates one lock per drain call. More importantly: the `_writeLock` section in TryCommitAndDrain currently blocks concurrent `SendFrameToWriter` calls. Removing it allows writers to pipeline: write+commit under `_writeLock` while drain proceeds under `_streamLock`.

**Evidence**: Plans 059/060 used this same per-frame commit + drain-only approach. Plan 059 Run 1 showed massive bulk improvements:
- 1ch×100KB: 0.24x → 1.37x (NC beats FRP!)
- 1ch×1MB: 0.74x → 1.11x
- 100ch×1KB: 0.19x → 0.64x

Those plans failed due to 1000ch timeouts (system instability) and additional changes (minimumSegmentSize, threshold changes). This plan isolates ONLY the per-frame commit change.

### Why game-tick is safe

Game-tick frames are 73B (64B msg) or 265B (256B msg). `toSend < 8192` → no drain triggered from `WriteChannel`. The only extra cost is `CommitPipeWriter` (~35-50ns) inside the existing `_writeLock`, adding ~4-5% CPU overhead. FlushLoop continues to batch and drain game-tick frames every 1ms, unchanged.

### Why TryDrain without _writeLock is safe

- `PipeReader.TryRead` is safe to call concurrently with `PipeWriter.GetSpan/Advance` — Pipe guarantees reader/writer independence after commit
- Data is committed per-frame, so TryRead sees all written data
- `_streamLock` serializes TCP writes (unchanged)
- FlushLoop still commits (no-op for already-committed data) and drains — safety net for residual data and credit grants

### Difference from Plans 059/060

| Aspect | Plan 059 | Plan 060 | Plan 064 |
|--------|----------|----------|----------|
| Per-frame commit | Yes | Yes | Yes |
| Drain-only (no _writeLock) | Yes | Yes | Yes |
| minimumSegmentSize | 65536 | 65536 | Default (4096) |
| Drain threshold | 1024 | 8192 | 8192 (unchanged) |
| ForceFlush path | Modified | Modified | Unchanged |
| 1000ch result | TimeoutException | TimeoutException R2 | TBD |

Plan 064 is the minimal isolated version — only the commit + drain changes, no other variables.

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md
- Game-tick 1ch×64B NC/FRP stays above 10x
- Game-tick 50ch×64B NC/FRP does not drop more than 5% from 10.68x (min 10.15x)
- No regressions in game-tick ratios
- All tests pass, 0 warnings
