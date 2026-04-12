# Plan 019: Aggressive Credit Grant Threshold (1/16 Window)

**Addresses:** Credit starvation dominates 81% of wall time (PROBLEMS.md Problem 1)

## Analysis

Credit grants currently fire at 25% of window (1MB for 4MB window). At 64KB chunks, the sender exhausts credits after ~62 chunks, then stalls waiting for the receiver to consume 1MB and grant credits back. Every message after the first 62 stalls.

Reducing the threshold to 1/16 (256KB) means grants fire 4x sooner. The sender resumes after waiting for ~4 chunks instead of ~16 chunks. Credit frames are tiny (16 bytes) and batched through FlushLoop's `_pendingCreditChannels` queue.

This differs from failed Plan 002 (1/8 threshold):
- Plan 002 ran before Plans 013/016/017 (ForceFlush, 1MB buffer, accumulation threshold)
- Plan 002 was evaluated with absolute numbers, not ratio-based methodology
- Current flush path is much faster, so faster credit grants translate to real throughput gains

## Change

**File:** `src/NetConduit/Internal/AdaptiveFlowControl.cs` — `RecordConsumptionAndGetGrant`

Replace:
```csharp
var quarterWindow = windowSize / 4;
```

With:
```csharp
var quarterWindow = windowSize / 16;
```

Single-line change. No structural modifications.

## Expected Behavior

| Scenario | Current | Plan 019 |
|----------|---------|----------|
| 1ch×1MB (16×64KB) | Stall after 62 chunks, wait for 1MB consumed | Stall after 62 chunks, wait for 256KB consumed (4x faster) |
| 10ch×100KB | 81% credit wait | ~50-60% credit wait (grants arrive sooner) |
| Game-tick 64B | Unaffected (never stalls) | Unaffected |

## Success Criteria

- Bulk throughput ratios improve (especially 1ch×100KB, 10ch×100KB, 1ch×1MB)
- Game-tick ratios do not regress
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B NC vs FRP does not drop more than 5%
