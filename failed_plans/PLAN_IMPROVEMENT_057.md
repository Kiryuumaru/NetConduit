# Plan 057: Size-Gated Direct-to-Stream Write Bypass

## What

Add a frame-size threshold to the direct-to-stream write bypass from Plan 056.
Only frames with `combinedLength >= 1024` (header + payload) bypass the Pipe
when conditions are met. Frames below this threshold always use the Pipe path,
preserving batching for small messages (game-tick).

## Why Plan 056 Failed

Plan 056 bypassed the Pipe for ALL frames when the Pipe was empty. This
eliminated FlushLoop batching for game-tick's 64B/256B messages (73B/265B
frames), causing each message to trigger an individual socket syscall instead
of being batched with hundreds of other messages. Result: game-tick 1ch×64B
collapsed from 15.69x to 2.68x vs FRP.

## Key Insight

- Game-tick messages: 64B (73B frame), 256B (265B frame) — below 1024B
- Bulk 1ch×1KB: 1024B data (1033B frame) — at/above 1024B
- Bulk 1ch×100KB/1MB: 64KB chunks (65545B frames) — well above 1024B

A threshold of 1024B cleanly separates game-tick (always batched) from bulk
(direct write when Pipe empty). TCP MTU is ~1460B, so frames >= 1024B
are already near segment-filling size and don't benefit from batching.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs`: Add `_pipeDataPendingDrain` field,
  modify `SendFrameToWriter` with size-gated direct write, update
  `FlushLoopAsync`/`ForceFlushPipeToStreamAsync`/`TryCommitAndDrainAsync`
  to track drain state
- `src/NetConduit/WriteChannel.cs`: Change `SendDataFrame` return to `bool`,
  skip TryCommitAndDrain/ForceFlush when direct write succeeded

## Implementation

### Direct write conditions (ALL must be true)

1. `combinedLength >= 1024` — only bulk-sized frames
2. `_unflushedDataBytes == 0` — no pending Pipe data
3. `Volatile.Read(ref _pipeDataPendingDrain) == 0` — no committed-but-undrained data
4. `_streamLock.Wait(0)` — stream available (non-blocking)

### Safety: _pipeDataPendingDrain flag

Same as Plan 056 v2: set to 1 after `CommitPipeWriter`, cleared to 0 after
`DrainPipeToStreamAsync` completes. Prevents direct write from sending data
to socket before committed Pipe data.

## Expected Impact

| Scenario | Expected | Reasoning |
|----------|----------|-----------|
| 1ch×1KB | +300-500% | Bypass 1ms FlushLoop for single 1033B frame |
| 1ch×100KB | +20-50% | Direct write for 64KB chunks when Pipe empty |
| 1ch×1MB | Neutral/+10% | Already has TryCommitAndDrain; direct write slightly faster |
| 10ch×1KB | Neutral | Pipe usually non-empty with 10 writers |
| 100ch×1KB | Neutral | Pipe always non-empty |
| Game-tick 64B | Neutral | 73B frames below 1024B threshold — always uses Pipe |
| Game-tick 256B | Neutral | 265B frames below 1024B threshold — always uses Pipe |

## Success Criteria

- Bulk throughput improved in 1-channel scenarios
- Game-tick ratios within 5% of baseline (1ch×64B ≥ 10x, 50ch×64B ≥ 10.15x)
- All tests pass
