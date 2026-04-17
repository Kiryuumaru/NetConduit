# Plan 048: Write-through to stream when pipe is drained (solo writer)

## Status: TESTING

## Hypothesis
For solo-writer scenarios with frames >= 1KB, bypass the pipe entirely and write directly to the stream. This eliminates the ~1ms FlushLoop timer wait for small bulk transfers (1ch×1KB, 1ch×100KB) where a single writer's data sits in the pipe waiting for the timer.

Key conditions for direct write:
- `_pipeDrained == true` (pipe is empty — no uncommitted data after last drain)
- `_unflushedDataBytes == 0` (double-check: no uncommitted writes since last commit)
- `_activeWriterCount <= 1` (solo writer — no batching benefit)
- `combinedLength >= 1024` (don't hurt game-tick small-message batching)
- `_streamLock` can be non-blocking acquired (no concurrent drain)
- `FlushMode == Batched` (don't interfere with Immediate mode)

## Why previous plans failed
- Plans 037-047: All tried to wake FlushLoop faster via signals/CAS. On 2-core benchmark, signal overhead and rapid drain cycles hurt multi-channel.
- Plan 048 difference: No signals, no timer changes. Writers go DIRECTLY to stream when safe. Zero overhead when conditions aren't met (just volatile reads).

## Files Modified
- `src/NetConduit/StreamMultiplexer.cs`: Add direct write path in SendFrameToWriter, track _pipeDrained
- `src/NetConduit/WriteChannel.cs`: Track _activeWriterCount

## Expected Impact
- 1ch×1KB: ~5-10x improvement (eliminate FlushLoop wait for single small transfer)
- 1ch×100KB: ~2-5x improvement
- 1ch×1MB: similar or slightly better (ForceFlush already handles large frames)
- Multi-channel: no change (activeWriterCount > 1 → pipe path)
- Game-tick: no change (frames < 1KB → pipe path)
