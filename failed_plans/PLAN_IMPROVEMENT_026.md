# Plan 026: Tiered ForceFlush with non-blocking try-drain at 65KB

## Change

Add a second, lower-threshold ForceFlush tier that uses non-blocking `_streamLock`
acquisition. When accumulated unflushed data reaches 65KB (but not yet 262KB) and the
frame is large (>= 65536), the writer thread attempts to drain — but only if the
stream lock is immediately available. If another drain is in progress, skip silently.

## Lesson From Plans 024/025

Plans 024 and 025 both tried signaling the FlushLoop to wake it earlier. Both failed
catastrophically in multi-channel scenarios because FlushLoop wakeups cause scheduling
interference on the 2-core benchmark setup.

The only safe drain mechanism is writer-side ForceFlush (Plan 013 pattern): the writer
thread itself does the drain, avoiding FlushLoop scheduling overhead.

## Files Modified

- `src/NetConduit/WriteChannel.cs` — ForceFlush condition
- `src/NetConduit/StreamMultiplexer.cs` — new `TryForceFlushPipeToStreamAsync` method

## Why It Should Help

For 1ch×100KB: First 64KB frame brings unflushed to 65544. New tier triggers
non-blocking try-drain. No contention → drain succeeds → 64KB reaches receiver
immediately instead of waiting up to 1ms for FlushLoop timer.

For multi-channel: non-blocking means at most one writer drains; others return
instantly. No blocking, no contention, no FlushLoop interference.

For game-tick: `toSend >= 65536` guard ensures small messages (64B, 256B) never
enter the check. Zero impact on game-tick.

## Tiered Flush Strategy

| Accumulated | Frame Size | Action | Blocking? |
|------------|-----------|--------|-----------|
| < 65KB | any | Wait for FlushLoop timer (1ms) | N/A |
| 65KB-262KB | >= 65536 | Try drain (non-blocking) | No |
| >= 262KB | >= 65536 | Force drain (blocking) | Yes |

## What Changes

```csharp
// WriteChannel.cs — after SendDataFrame:
// Tier 1: blocking drain for large accumulation (existing)
if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)
    await _multiplexer.ForceFlushPipeToStreamAsync(cancellationToken).ConfigureAwait(false);
// Tier 2: non-blocking try-drain for medium accumulation (new)
else if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 65536)
    await _multiplexer.TryForceFlushPipeToStreamAsync(cancellationToken).ConfigureAwait(false);
```

## Success Criteria

- 1ch×100KB ratio improves (baseline: 0.23x vs FRP)
- Multi-channel scenarios do not regress
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
