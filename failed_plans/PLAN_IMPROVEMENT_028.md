# Plan 028: Remove frame-size guard from ForceFlush + reduce FlushInterval to 250µs

## Change

Two related changes to reduce flush latency:

1. **Remove `toSend >= 65536` from ForceFlush condition** — allow any frame to trigger
   caller-side drain when 262KB+ is accumulated in the Pipe.

2. **Reduce FlushInterval from 1ms to 250µs** — FlushLoop drains 4x more often,
   reducing max wait for frames that don't trigger ForceFlush.

## Root Cause Analysis

For 10ch×100KB: each channel sends 64KB + 36KB (two writes). The 64KB frames
eventually accumulate to 262KB and trigger ForceFlush. But the 36KB tail frames
NEVER trigger ForceFlush because they fail `toSend >= 65536`. 360KB of tail data
(10 × 36KB) sits in the Pipe waiting up to 1ms for FlushLoop.

For 1ch×100KB: total data = 100KB < 262KB. ForceFlush never triggers at all.
Both writes (64KB + 36KB) wait for FlushLoop. Full 1ms penalty.

## Lesson From Plans 024-027

- Plan 024/025: Signaling FlushLoop causes scheduling interference.
  This plan uses ForceFlush (caller drains), NOT FlushLoop signaling.
- Plan 026: Lock reordering (_streamLock→_writeLock) causes contention spiral.
  This plan keeps the existing lock order (_writeLock→_streamLock).
- Plan 027: 250µs alone was insufficient — doesn't fix the ForceFlush gap.

## Files Modified

- `src/NetConduit/WriteChannel.cs` — ForceFlush condition
- `src/NetConduit/Models/MultiplexerOptions.cs` — FlushInterval default

## Game-Tick Safety Analysis

With 250µs FlushInterval:
- 50ch × 64B: 73B/frame × 50ch × ~21K msg/s/ch / (1/0.00025) = ~18KB per cycle
- Need 262144 / 18250 = 14.4 cycles (3.6ms) to reach ForceFlush threshold
- FlushLoop drains every 250µs — ForceFlush NEVER triggers during game-tick
- Zero impact from removing frame-size guard; margin is 14x

## What Changes

```csharp
// WriteChannel.cs — ForceFlush condition:
// Before:
if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)

// After:
if (Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)
```

```csharp
// MultiplexerOptions.cs — FlushInterval:
// Before:
public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(1);

// After:
public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMicroseconds(250);
```

## Success Criteria

- Bulk throughput ratios improve (especially 100KB scenarios)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
