# Plan 058: High-threshold direct write bypass (≥65536B)

## Change

When a frame is ≥65536 bytes (combinedLength including 9-byte header), the Pipe is empty
(`_unflushedDataBytes == 0`), no committed data is pending drain, and the stream is
immediately available (`_streamLock.Wait(0)` succeeds), write the frame directly to the
TCP stream, bypassing the Pipe entirely.

This is Plan 057 with a **higher threshold** (65536 vs 1024). The higher threshold:
- Protects ALL game-tick frames (73–265B, far below 65536)
- Protects ALL 1KB bulk scenarios (1033B, far below 65536)
- Only activates for ≥64KB frames, where Pipe copy overhead matters most

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — SendFrameToWriter restructured with direct write
  path; `_pipeDataPendingDrain` flag for commit-drain window safety; FlushLoop/ForceFlush/
  TryCommitAndDrain set/clear the flag
- `src/NetConduit/WriteChannel.cs` — SendDataFrame returns bool; skip ForceFlush/
  TryCommitAndDrain when direct write succeeded

## Analysis

### Why Plan 057 failed (threshold 1024)

Plan 057 set threshold at 1024B. This caused:
- 1ch×1KB (1033B frames): direct write for every frame, per-frame syscalls slower
  than Pipe batching → 0.11x (was 0.17x)
- 1ch×100KB (65545B + 36873B): both chunks above threshold, per-chunk syscalls → 0.19x (was 0.24x)
- 1000ch×256B: TimeoutException (system flake — Raw TCP also had IOExceptions)

But Plan 057 IMPROVED:
- 1ch×1MB: 0.74x → 1.11x (eliminates Pipe copy for large frames)
- 10ch×1KB: 0.79x → 1.31x (contention-driven Pipe fallback works)
- 100ch×1MB: 0.55x → 0.88x

### Why threshold 65536 fixes the regressions

| Scenario | Frame size | ≥1024? | ≥65536? | Plan 057 | Plan 058 |
|----------|-----------|--------|---------|----------|----------|
| Game-tick 64B | 73B | NO | NO | Pipe ✓ | Pipe ✓ |
| Game-tick 256B | 265B | NO | NO | Pipe ✓ | Pipe ✓ |
| Bulk 1KB | 1033B | YES→direct | NO | REGRESSED | Pipe (safe) |
| Bulk 100KB chunk1 | 65545B | YES→direct | YES→direct | Mixed | Direct (help) |
| Bulk 100KB chunk2 | 36873B | YES→direct | NO | Mixed | Pipe (safe) |
| Bulk 1MB chunks | 65545B each | YES→direct | YES→direct | BIG WIN | BIG WIN |

### Safety: _pipeDataPendingDrain flag

Prevents direct write during FlushLoop's commit→drain window:
- Set to 1 immediately after CommitPipeWriter (data is committed, not yet drained)
- Cleared to 0 after DrainPipeToStreamAsync completes
- Direct write checks flag is 0 before proceeding

Without this flag, direct write could send data before previously committed Pipe data,
violating frame ordering.

### Lock restructuring (same as Plan 057 v2)

```
lock (_writeLock):
  Check: combinedLength >= 65536 && _unflushedDataBytes == 0
         && pipeDataPendingDrain == 0 && _streamLock.Wait(0)
  If true: rent ArrayPool buffer, copy header+data, release _writeLock
  If false: standard Pipe path (GetSpan/CopyTo/Advance)

After _writeLock (if direct):
  Write buffer to stream (under _streamLock only)
  Return buffer, release _streamLock
```

_writeLock held only for the decision + buffer copy (fast), not during I/O.

## Expected Impact

| Scenario | Baseline | Expected |
|----------|----------|----------|
| 1ch×1KB | 0.17x | 0.17x (unchanged) |
| 1ch×100KB | 0.24x | ~0.28x (first chunk direct) |
| 1ch×1MB | 0.74x | ~1.11x (all chunks direct) |
| 10ch×1KB | 0.79x | 0.79x (unchanged) |
| 10ch×100KB | 0.70x | ~0.75x (partial direct) |
| 10ch×1MB | 0.72x | ~0.73x (contention) |
| 100ch×1KB | 0.19x | 0.19x (unchanged) |
| 100ch×100KB | 0.53x | ~0.55x (partial direct) |
| 100ch×1MB | 0.55x | ~0.88x (contention fallback) |
| Game-tick all | baseline | baseline (unchanged) |

## Success Criteria

- Bulk throughput ratios improve, especially 1ch×1MB and 100ch×1MB
- Game-tick ratios do not regress (below 65536 threshold → always Pipe path)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B NC vs FRP does not drop more than 5% from baseline
- All tests pass
