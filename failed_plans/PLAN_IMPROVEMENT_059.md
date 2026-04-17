# Plan 059: Per-Frame Commit + Lock-Free TryDrain + Larger Pipe Segments

## What

Three orthogonal changes that address different bottlenecks:

1. **Per-frame CommitPipeWriter** inside `SendFrameToWriter` — data becomes visible to PipeReader immediately after each frame write
2. **Lock-free TryDrainAsync** — remove `_writeLock` from the drain helper so it only acquires `_streamLock` (non-blocking)
3. **minimumSegmentSize: 65536** — reduce Pipe segment count for multi-channel drains, fewer `WriteAsync` syscalls

## Why

### Evidence from Failed Plans

| Plan | Approach | Failure | Lesson |
|------|----------|---------|--------|
| 054 | Lower TryCommitAndDrain threshold 8192→1024 | _writeLock contention in TryCommitAndDrain blocked concurrent writers; 10ch×1KB -58% | REMOVING _writeLock from drain path eliminates this failure mode |
| 052 | Inline commit + double-commit in TryCommitAndDrain | Double commit overhead; 1ch×1MB -54% | Remove commit FROM TryCommitAndDrain entirely — commit only in SendFrameToWriter |
| 056-058 | Direct-to-stream bypass | Loses Pipe write-ahead pipelining | This plan KEEPS the Pipe — no bypass |

### Mechanistic Analysis

**1ch×1KB bottleneck (current 0.17x FRP):**
- Frame = 1033B (< 8192 threshold) → no drain attempt → waits up to 1ms for FlushLoop
- RTT: write(~50ns) + FlushLoop-wait(~500us avg) + drain(~50us) ≈ 550-1050us
- With per-frame commit + TryDrain: write(~50ns) + commit(~35ns) + drain(~50us) ≈ 100us
- Expected: ~5x improvement in single-channel 1KB throughput

**Multi-channel segment reduction:**
- 100ch×1KB: 100 frames of 1033B between drains
- Default 4KB segments: ~3 frames/segment → ~34 segments → 34 WriteAsync calls
- 65KB segments: ~63 frames/segment → ~2 segments → 2 WriteAsync calls (17x fewer syscalls)

**Game-tick safety:**
- Game-tick frames: 73B (64B msg) and 265B (256B msg) — ALL below 1024B threshold
- TryDrain NOT called for game-tick → zero impact on game-tick batching

### Why TryDrain Without _writeLock is Safe

- `PipeWriter.FlushAsync()` with `pauseWriterThreshold: 0` completes synchronously (~35ns)
- Adding it to `SendFrameToWriter` (already under `_writeLock`) is negligible 
- `TryDrainAsync` only needs `_streamLock` to serialize writes to the TCP stream
- PipeReader.TryRead is thread-safe with PipeWriter (Pipe guarantees reader/writer independence)
- Multiple writers: one succeeds at drain, rest skip instantly (`_streamLock.Wait(0)` returns false)
- FlushLoop remains as safety net for residual committed-but-undrained data

## Files Modified

| File | Method | Change |
|------|--------|--------|
| StreamMultiplexer.cs | Constructor (~line 442) | Add `minimumSegmentSize: 65536` to PipeOptions |
| StreamMultiplexer.cs | SendFrameToWriter | Add `CommitPipeWriter(writer)` after `writer.Advance()` |
| StreamMultiplexer.cs | TryCommitAndDrainAsync → TryDrainAsync | Remove `_writeLock` block, keep `_streamLock` + drain only |
| WriteChannel.cs | Post-send drain | Replace ForceFlush/TryCommitAndDrain with `TryDrainAsync` for `toSend >= 1024` |

## Success Criteria

- Game-tick 1ch×64B NC/FRP stays above 10x (baseline 15.69x)
- Game-tick 50ch×64B NC/FRP does not drop >5% from 10.68x (min 10.15x)
- Bulk throughput improves (especially 1ch×1KB, 100ch×1KB)
- All tests pass, 0 warnings
