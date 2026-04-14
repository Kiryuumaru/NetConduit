# Plan 021: Revert Plan 017 Accumulation Threshold (Keep Plan 016 Only)

**Addresses:** Single-channel throughput regression from Plan 017's accumulation threshold

## Analysis

Plan 016 (1MB read buffer) alone showed 1ch×1MB: 0.64x→1.16x (+81% vs FRP).
Plan 017 (256KB accumulation threshold) alone showed 10ch×100KB: +276%.
Combined, 1ch×1MB dropped to 0.93x — Plan 017's delay hurts single-channel.

The accumulation threshold (256KB before ForceFlush) causes the first 3 frames (192KB)
to sit in the Pipe buffer until either the 4th frame triggers ForceFlush or the 1ms timer fires.
For single-channel, this adds ~1ms latency per batch.

Hypothesis: Plan 016 alone gives better overall balance than 016+017 combined.

## Change

**File:** `src/NetConduit/StreamMultiplexer.cs`
- Remove `_unflushedDataBytes` field
- Remove increment in `SendFrameToWriter`
- Remove reset in `FlushLoopAsync` and `ForceFlushPipeToStreamAsync`

**File:** `src/NetConduit/WriteChannel.cs`
- Change `if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)` back to `if (toSend >= 65536)`

## Success Criteria

- 1ch×1MB ratio improves vs current (Plan 016+017) baseline
- Multi-channel ratios acceptable (some regression from losing Plan 017 is expected)
- Game-tick ratios stable
