# Plan 012: Pending-Bytes Flush Signal Threshold

**Addresses:** 0-1ms FlushLoop timer latency for bulk writes while preserving game-tick batching

## Change

**File:** `src/NetConduit/StreamMultiplexer.cs`

Track accumulated pending bytes in the write Pipe. Signal the FlushLoop when pending bytes exceed 2KB. Reset after drain.

For bulk throughput (100KB frames): 100KB > 2KB → immediate FlushLoop signal → eliminates timer delay.
For game-tick (64B messages): 73B chunks accumulate to 2KB (~28 messages) before signal → preserves batch efficiency.

### Implementation

1. Add `private long _pendingBytes` field
2. In `SendFrameToWriter`: increment `_pendingBytes` by frame size; if >= 2048, call `SignalFlush()`
3. In FlushLoop: reset `_pendingBytes` to 0 after drain

### Differs from Plan 003

Plan 003 used 64KB threshold → signaled only after 64KB accumulated → still too slow for single-channel 100KB transfers (needed 100KB to trigger).

This plan uses 2KB threshold → signals after any reasonably-sized write → good for both bulk and multi-channel.

## Success Criteria

- Bulk throughput improves toward 2x baseline
- Game-tick 1ch×64B stays above 1,200,000 msg/s (baseline: 1,417,077)
- Game-tick 50ch×64B does not regress more than 5% (baseline: 951,194)
