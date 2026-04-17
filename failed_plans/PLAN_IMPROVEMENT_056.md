# Plan 056: Direct-to-stream write bypass when Pipe is empty

## What

Add an opportunistic fast path in `SendFrameToWriter` that writes data frames directly to the underlying stream (socket) when:
1. The Pipe has no pending unflushed data (`_unflushedDataBytes == 0`)
2. The stream lock is available (`_streamLock.Wait(0)` succeeds)

When both conditions are met, the frame header + payload are encoded into a pooled buffer and written synchronously to the stream, completely bypassing the Pipe, CommitPipeWriter, and FlushLoop.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — Add direct write fast path in `SendFrameToWriter`, change return type to `bool` (true = sent direct)
- `src/NetConduit/WriteChannel.cs` — Skip TryCommitAndDrain/ForceFlush when frame was sent direct

## Analysis

### Root Cause of Poor Single-Channel Bulk Throughput

For frames < 8KB (e.g., 1ch×1KB benchmark), the data path is:
1. Writer copies data into shared Pipe under `_writeLock`
2. FlushLoop wakes every 1ms, commits Pipe, drains to socket

The 1ms FlushLoop timer imposes a hard throughput ceiling: ~1KB/ms = ~1 MB/s for 1ch×1KB. Measured: 1.4 MB/s. FRP/Yamux achieves 8.2 MB/s because each frame is written to the socket immediately via a goroutine channel.

For frames ≥ 8KB, TryCommitAndDrain fires after SendDataFrame. But this still involves: Pipe.GetSpan → memcpy → Advance → Monitor.TryEnter → CommitPipeWriter(Pipe.FlushAsync) → Monitor.Exit → _streamLock.Wait(0) → Pipe.TryRead → stream.WriteAsync → AdvanceTo. Six operations on two locks.

### Why Direct Write Works

Direct write reduces the path to: ArrayPool.Rent → memcpy → stream.Write → ArrayPool.Return. One lock (_streamLock), one syscall, no Pipe overhead.

The `_unflushedDataBytes == 0` guard ensures the Pipe is empty, so there's no data reordering. The `_streamLock.Wait(0)` guard prevents concurrent socket writes (from FlushLoop drain).

### Why Previous Plans Failed (and This Is Different)

All 55 failed plans attempted to reduce latency by modifying flush/drain behavior within the existing Pipe architecture:
- Writer-side drain (Plans 043-052, 054): _writeLock contention between writers and FlushLoop
- Signal-based flush (Plans 037-039, 045-048): scheduling overhead on 2 cores
- Timer reduction (Plans 032-036): CPU spin on 2 cores
- Transport buffers (Plan 055): removed natural backpressure

This plan is fundamentally different: it bypasses the Pipe entirely rather than trying to drain it faster. No new locks, no new signals, no timer changes. The existing Pipe path remains unchanged as the fallback.

### Precedent

`SendHandshakeAsync` already writes directly to the stream (line ~1462). The pattern is proven to work.

### Safety Analysis

- **Frame ordering**: Frames from the same channel are always sequential (one WriteAsync at a time per channel). Different channels can interleave freely. Direct and Pipe frames may interleave across channels — this is correct.
- **Concurrency**: `_streamLock` prevents concurrent socket writes. `_unflushedDataBytes == 0` ensures no Pipe data is pending that should go first.
- **Error handling**: Stream write exceptions are caught, stored in `_writeError`, and propagated.
- **Fallback**: If either condition fails (Pipe has data, or stream is busy), the existing Pipe path runs unchanged.

### Expected Impact

| Scenario | Current | Expected | Reason |
|----------|---------|----------|--------|
| 1ch×1KB | 1.4 MB/s (0.17x) | ~8-15 MB/s (~1.0x) | Bypass 1ms FlushLoop entirely |
| 1ch×100KB | 100.1 MB/s (0.24x) | ~300-500 MB/s (~0.8x) | Two direct writes instead of Pipe+drain |
| 1ch×1MB | 828.5 MB/s (0.74x) | ~1000-1200 MB/s (~1.0x) | 16 direct writes, no Pipe overhead |
| 10ch×1KB | 15.5 MB/s (0.79x) | ~15-20 MB/s (~0.8-1.0x) | One direct, 9 batched — marginal |
| Game-tick | 15.69x | ~15x | Falls back to Pipe (many small messages, Pipe always has data) |

## Success Criteria

- Bulk throughput: NC vs FRP ratios improve (especially 1ch scenarios)
- Game-tick: NC vs FRP stays ≥ 10x for 1ch×64B, ≥ 10.15x for 50ch×64B
- All tests pass
