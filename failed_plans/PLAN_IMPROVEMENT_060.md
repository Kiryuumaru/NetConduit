# Plan 060: Per-frame Commit + Lock-free Drain + 65KB Pipe Segments

## What

Four changes to the write/flush/drain hot path:

1. **Per-frame CommitPipeWriter** — Call `CommitPipeWriter(writer)` inside `SendFrameToWriter` under existing `_writeLock`. Makes data visible to PipeReader immediately (~35ns synchronous with `pauseWriterThreshold: 0`).

2. **New TryDrainOnlyAsync** — A drain method that only acquires `_streamLock` (no `_writeLock`). Since data is already committed per-frame, no commit step needed in the drain path.

3. **WriteChannel drain: TryDrainOnlyAsync** — Replace `TryCommitAndDrainAsync` call with `TryDrainOnlyAsync`. Same threshold (`toSend >= 8192`). Game-tick 64B frames do NOT trigger drain (toSend=64 < 8192).

4. **Pipe minimumSegmentSize: 65536** — Reduces segment count when many small frames accumulate, resulting in fewer TCP `WriteAsync` syscalls per drain cycle.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — Per-frame commit in `SendFrameToWriter`, new `TryDrainOnlyAsync`, `minimumSegmentSize: 65536`
- `src/NetConduit/WriteChannel.cs` — Call `TryDrainOnlyAsync` instead of `TryCommitAndDrainAsync`

## Analysis

### Why this helps bulk throughput

**Root cause of slow bulk**: In baseline, `TryCommitAndDrainAsync` acquires `_writeLock` to commit, then `_streamLock` to drain. While draining (TCP write, ~100µs), `_writeLock` is free but `_streamLock` is held. The next write can proceed (GetSpan+Advance under `_writeLock`) but its drain attempt via `Wait(0)` on `_streamLock` fails. Data waits for FlushLoop's 1ms timer.

**Plan 060 fix**: Per-frame commit makes data visible to PipeReader inside `SendFrameToWriter`. `TryDrainOnlyAsync` only needs `_streamLock`. Writers write+commit while a concurrent drain is in progress. This **pipelines writes and drains** — the Pipe decouples them.

**Evidence from Plan 059**: Run 1 showed massive improvements using this pipelining approach:
- 1ch×100KB: 0.24x → 1.37x (NC beats FRP!)
- 1ch×1MB: 0.74x → 1.11x (NC beats FRP!)
- 100ch×1KB: 0.19x → 0.64x (3.4x improvement)

### Why this won't break game-tick

Game-tick uses 64B messages. `toSend = 64 < 8192`, so **no drain is triggered** from WriteChannel. The write path is identical to baseline except for per-frame `CommitPipeWriter` (~35ns). At 1M+ msg/s, that's ~35ms/second additional overhead (3.5%). Negligible.

The FlushLoop continues to batch and drain game-tick frames every 1ms, unchanged.

### Why this won't break 1000ch×64B

Plan 059 failed at 1000ch because `TryDrainAsync` was called per-frame for ALL sizes (threshold 1024). With 1000 channels × rapid fire = thundering herd on `_streamLock.Wait(0)`.

Plan 060 keeps the baseline threshold of 8192. Game-tick 64B frames never trigger drain. The drain contention at 1000ch is identical to baseline.

### minimumSegmentSize impact

With default 4096B segments, small frames (1KB = 1033B including header) pack ~3-4 per segment. 1000 accumulated frames = ~250-333 segments = 250-333 TCP `WriteAsync` calls per drain.

With 65536B segments, ~63 small frames per segment. 1000 frames = ~16 segments = 16 TCP writes. ~18x fewer syscalls.

This helps `WriteBufferToStreamAsync`'s multi-segment foreach loop and was validated in Plan 059.

### References

- Plan 059: Proved pipelining approach works (massive bulk gains) but failed at 1000ch due to low drain threshold
- Plan 052: Failed due to unconditional per-frame commit causing double-commit; Plan 060 avoids double-commit by separating TryDrainOnly from TryCommitAndDrain
- Plan 054: Failed due to lowering TryCommitAndDrain threshold to 1024; Plan 060 keeps 8192

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md
- Game-tick 1ch×64B NC/FRP stays above 10x
- Game-tick 50ch×64B NC/FRP does not drop more than 5% from docs/benchmarks.md (10.68x → min 10.15x)
- No regressions in game-tick ratios
- Note: baseline system currently shows 1000ch×256B TimeoutException even without changes; this is a known system issue
