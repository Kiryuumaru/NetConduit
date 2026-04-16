# Plan 061: Atomic unflushed tracking + larger pipe segments

## Problem

`_unflushedDataBytes` grows monotonically in the current code because `TryCommitAndDrainAsync` 
never decrements it after successfully draining data. Only `FlushLoopAsync` and 
`ForceFlushPipeToStreamAsync` reset it to 0 (inside `_writeLock`).

For bulk 65KB frames, this causes ForceFlush to trigger every 4th frame:
- Frame 1: unflushed=65545, TryCommitAndDrain succeeds (but unflushed NOT decremented)
- Frame 2: unflushed=131090, TryCommitAndDrain succeeds
- Frame 3: unflushed=196635, TryCommitAndDrain succeeds
- Frame 4: unflushed=262180 ≥ 262144 → ForceFlush triggers!

ForceFlush is expensive: it holds `_writeLock` during the entire drain I/O (~30µs+), 
blocking ALL channel writes during that time.

## Changes

### 1. Atomic `_unflushedDataBytes` tracking (StreamMultiplexer.cs)
- Change `_unflushedDataBytes += combinedLength` to `Interlocked.Add(ref _unflushedDataBytes, combinedLength)` in SendFrameToWriter
- Change `_unflushedDataBytes = 0` to `Interlocked.Exchange(ref _unflushedDataBytes, 0)` in FlushLoop and ForceFlush
- Add `Interlocked.Add(ref _unflushedDataBytes, -(long)buffer.Length)` in TryCommitAndDrainAsync after successful drain

This makes all accesses atomic and allows TryCommitAndDrain to correctly track how much unflushed data remains.

### 2. Larger pipe segments (StreamMultiplexer.cs)
- Add `minimumSegmentSize: 65536` to PipeOptions
- Reduces pipe segment count for accumulated small messages (game-tick)
- Game-tick drain: many frames in fewer segments → fewer WriteAsync calls in WriteBufferToStreamAsync

## Why This Should Help

### Bulk throughput
- Eliminates spurious ForceFlush blocks: instead of 4 ForceFlush per 16 frames (1MB), expect 0 ForceFlush when drains succeed
- Each ForceFlush blocks _writeLock for ~30µs (stream I/O time)
- 4 × 30µs = 120µs saved per 1MB → ~10% throughput improvement

### Game-tick
- Larger segments: 1000 × 73B messages → 2 segments instead of 18 → fewer WriteAsync calls
- Neutral or slight improvement

### 1000ch safety
- No lock changes, no drain threshold changes, no protocol changes
- ForceFlush still triggers as safety net when drains genuinely fail
- FlushLoop unchanged (1ms timer)

## Files Modified
- `src/NetConduit/StreamMultiplexer.cs`: PipeOptions, SendFrameToWriter, TryCommitAndDrainAsync, FlushLoopAsync, ForceFlushPipeToStreamAsync

## Success Criteria
- Bulk throughput NC/FRP ratios improved vs docs/benchmarks.md
- Game-tick ratios not regressed
- Game-tick 1ch×64B stays above 10x
- Game-tick 50ch×64B within 5% of docs 10.68x
- No 1000ch×64B timeout
