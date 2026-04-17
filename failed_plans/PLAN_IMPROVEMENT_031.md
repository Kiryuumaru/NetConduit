# Plan 031: Lower per-frame ForceFlush guard from 64KB to 16KB

## Change

Reduce the per-frame size guard in the ForceFlush condition from 65536 (64KB) to 16384 (16KB).
The accumulation threshold stays at 262144 (256KB).

## Root Cause Analysis

For 100KB transfers, each channel writes 64KB + 36KB. The 64KB frame meets the
per-frame guard and can trigger ForceFlush. The 36KB tail frame does NOT meet
the 64KB guard, so all tail writes wait for FlushLoop regardless of accumulated data.

For 10ch×100KB: 10 × 36KB = 360KB of tail data waits for FlushLoop (1ms cycle).
For 100ch×100KB: 100 × 36KB = 3.6MB of tail data blocked from ForceFlush.
Lowering the guard to 16KB enables these 36KB writes to trigger caller-side drain.

## Game-Tick Safety Analysis

Game-tick messages: 64B or 256B. With 9-byte header: 73B or 265B.
Per-frame guard 16384 >> 265. **ForceFlush never triggers for game-tick messages.**

Even at 1000ch×256B: each message toSend = 256 < 16384. No ForceFlush.
Game-tick is completely unaffected.

## Regression Safety Analysis

- 1ch×1MB: 64KB frames >= 16KB → same trigger pattern as before. **No change.**
- 10ch×1MB: same. **No change.**
- 100ch×1MB: same. **No change.**
- 1ch×1KB: 1KB < 16KB → no ForceFlush as before. **No change.**
- 1ch×100KB: 64KB frame >= 16KB but accumulated < 256KB. 36KB frame >= 16KB
  but accumulated (100KB) < 256KB. **No change for single-channel 100KB.**

## Files Modified

- `src/NetConduit/WriteChannel.cs` — per-frame ForceFlush guard

## What Changes

```csharp
// Before:
if (toSend >= 65536 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)

// After:
if (toSend >= 16384 && Volatile.Read(ref _multiplexer._unflushedDataBytes) >= 262144)
```

## Success Criteria

- 10ch×100KB and 100ch×100KB bulk ratios improve
- Game-tick ratios unchanged (1ch×64B >10x, 50ch×64B within 5%)
- No regressions in 1ch×1MB or other scenarios
