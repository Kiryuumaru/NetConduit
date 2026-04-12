# Plan 020: Direct-to-Stream Write for Large Frames

**Addresses:** Pipe buffer overhead on the ForceFlush hot path (Problems 2 & 3)

## Analysis

For large frames (≥65536 bytes) that trigger ForceFlush, the data currently follows:
1. `SendFrameToWriter`: memcpy header+payload into Pipe buffer under `_writeLock`
2. `ForceFlushPipeToStreamAsync`: commit PipeWriter, then drain PipeReader to TCP under `_streamLock`

This adds one unnecessary memory copy and multiple lock transitions. For 64KB frames, that's
64KB memcpy + Pipe commit + Pipe TryRead + buffer traversal per frame.

**Key insight:** When ForceFlush is triggered, the caller is already synchronous and willing to
wait for the I/O. We can skip the Pipe entirely and write the frame directly to the TCP stream.

## Change

**File:** `src/NetConduit/StreamMultiplexer.cs`

Add a new method `SendFrameDirectToStreamAsync` that:
1. Acquires `_streamLock`
2. First drains any pending Pipe data (to maintain frame ordering)
3. Writes the frame header+payload directly to the stream
4. Flushes the stream

**File:** `src/NetConduit/WriteChannel.cs`

For the ForceFlush path (toSend ≥ 65536 AND unflushedDataBytes ≥ 262144), call
`SendFrameDirectToStreamAsync` instead of `SendDataFrame` + `ForceFlushPipeToStreamAsync`.

## Expected Behavior

| Scenario | Current | Plan 020 |
|----------|---------|----------|
| 1ch×1MB (16×64KB) | 16 memcpy to Pipe + 16 Pipe drains | 16 direct TCP writes (skip Pipe) |
| Game-tick 64B | FlushLoop timer | FlushLoop timer (no change, frames < 65536) |

## Success Criteria

- Bulk throughput ratios improve (especially single-channel)
- Game-tick ratios do not regress
- Game-tick 1ch×64B NC vs FRP stays above 10x
