# Plan 030: Lower ForceFlush accumulation threshold from 256KB to 128KB

## Change

Reduce the `_unflushedDataBytes` threshold from 262144 (256KB) to 131072 (128KB)
in the ForceFlush condition. The per-frame guard (`toSend >= 65536`) stays unchanged.

## Root Cause Analysis

The current 256KB threshold delays caller-side drains. For 10ch×100KB (total 1MB):
- 10 parallel writers each send 64KB + 36KB
- ForceFlush requires 4 writers' first frames (256KB) before it triggers
- At 128KB, only 2 writers' first frames trigger ForceFlush
- Earlier drains = less data waiting for FlushLoop = lower latency

For 1ch×1MB (16 × 64KB):
- 256KB threshold: ForceFlush every 4 frames, 4 drains of 256KB each
- 128KB threshold: ForceFlush every 2 frames, 8 drains of 128KB each
- More frequent drains keep the Pipe smaller, reducing memory pressure

## Lesson From Plans 028-029

- Plan 028: Removing per-frame guard caused crashes (small-message ForceFlush)
- Plan 029: ForceFlush on CloseAsync created severe lock contention with FlushLoop
- This plan keeps the per-frame guard intact (only frames >= 64KB trigger)
- This plan does NOT add any new ForceFlush call sites — same code path as Plan 017

## Game-Tick Safety Analysis

ForceFlush requires `toSend >= 65536`. Game-tick messages are 64B or 256B.
`toSend` for game-tick is always < 65536. **ForceFlush never triggers**, regardless
of accumulation threshold. Zero impact on game-tick.

## Files Modified

- `src/NetConduit/WriteChannel.cs` — ForceFlush accumulation threshold

## What Changes

```csharp
// Before:
if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)

// After:
if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 131072)
```

## Success Criteria

- Bulk throughput ratios improve (especially 10ch and 100ch scenarios)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
