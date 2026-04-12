# Plan 025: Signal FlushLoop on accumulated unflushed bytes threshold

## Change

In `SendFrameToWriter`, signal the FlushLoop when `_unflushedDataBytes >= 65536` (64KB accumulated).
This creates a tiered flush strategy:
- < 64KB accumulated: wait for FlushLoop timer (1ms) — batches small messages
- 64KB-256KB accumulated: signal FlushLoop for prompt drain
- > 256KB accumulated with large frame: ForceFlush on caller thread (existing behavior)

## Lesson From Plan 024

Plan 024 signaled based on per-frame payload size (>= 1024 bytes). This caused
100ch×1KB to collapse (-86%): 100 channels each signaling individually overwhelmed
the FlushLoop with signal storms. Threshold must be based on TOTAL accumulated data,
not individual frame size, to naturally batch many small writes.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — `SendFrameToWriter` method

## Why It Should Help

For single-channel or few-channel 100KB transfers, the first 64KB frame accumulates
65544 bytes (>= 65536), triggering a FlushLoop signal. The drain happens in microseconds
instead of waiting up to 1ms. This directly fixes the 1ch×100KB bottleneck.

For 100ch×1KB (the scenario that killed Plan 024): each frame adds ~1033 bytes.
After ~63 channels write, accumulated bytes reach 65536 → 1 signal. Only ~1-2 signals
total instead of 100. Natural batching preserved.

For game-tick at 64B × 50ch: each frame is 73 bytes. Need ~898 frames to reach 65536.
With 50ch, that's ~18 messages per channel — signals every ~0.9ms, nearly identical
to the current 1ms timer.

## What Changes

```csharp
// Before:
_pendingFlush = true;
if (_options.FlushMode == FlushMode.Immediate || forceFlush)
    SignalFlush();

// After:
_pendingFlush = true;
if (_options.FlushMode == FlushMode.Immediate || forceFlush || _unflushedDataBytes >= 65536)
    SignalFlush();
```

## Success Criteria

- 1ch×100KB ratio improves (baseline: 0.23x vs FRP)
- 100ch×1KB does not regress (baseline: 1.05x vs FRP)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
