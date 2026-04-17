# Plan 046: ForceFlush on first write to empty pipe for bulk frames

## Change

When a data frame >= 1KB is the first write to an empty pipe, immediately call ForceFlushPipeToStreamAsync from the writer thread. This eliminates the 1ms FlushLoop timer delay for single-shot bulk transfers without affecting game-tick batching.

## Files Modified

- src/NetConduit/StreamMultiplexer.cs: SendDataFrame returns bool (wasEmpty), SendFrameToWriter returns bool
- src/NetConduit/WriteChannel.cs: WriteAsync calls ForceFlush when wasEmpty and toSend >= 1024

## Why It Should Help

For 1ch x 1KB: pipe is empty when the single frame arrives. ForceFlush drains immediately (0ms delay vs 1ms FlushLoop timer). Expected major improvement.

For game-tick (64B, 256B): below 1024B threshold, completely unaffected, preserves 17x advantage.

## Key Differences from Plans 043-045

- Plans 043-044: ForceFlush based on frame size and solo-writer detection. Fires too often in multi-channel.
- Plan 045: Signal FlushLoop on empty pipe. FlushLoop cycles rapidly after each commit resets unflushed to 0.
- Plan 046: ForceFlush from WRITER THREAD only when pipe was empty. When pipe is empty, FlushLoop has nothing to drain and does not hold _streamLock. Zero contention. Multi-channel: first writer to empty pipe ForceFlushes all accumulated frames, others skip. 1KB threshold protects game-tick.

## Success Criteria

- All tests pass
- Bulk throughput ratios improve
- Game-tick ratios do not regress
- 1ch x 64B game-tick stays above 10x
- 50ch x 64B game-tick no more than 5% drop
