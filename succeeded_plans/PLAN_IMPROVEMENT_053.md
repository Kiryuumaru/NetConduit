# Plan 053: Remove upper bound from TryCommitAndDrain condition

## Change

Remove the `&& toSend < 65536` upper bound from the TryCommitAndDrain condition
in `WriteChannel.cs`, so that frames >= 8KB that do NOT meet the ForceFlush
threshold still get a non-blocking drain attempt.

## Files Modified

- `src/NetConduit/WriteChannel.cs` — TryCommitAndDrain condition

## Analysis

Current conditions in WriteChannel.WriteAsync after SendDataFrame:

```csharp
if (toSend >= 65536 && accumulated >= 262144)
    ForceFlush           // blocking drain
else if (toSend >= 8192 && toSend < 65536)
    TryCommitAndDrain    // non-blocking drain
```

For exactly 65536-byte frames (the benchmark's 64KB chunk size):
- ForceFlush: needs accumulated >= 262144. For a single channel, the first 3 frames
  accumulate ~196KB which is below threshold. Only the 4th frame triggers ForceFlush.
- TryCommitAndDrain: `toSend < 65536` is FALSE for 65536. Falls through.
- Result: frames 1-3 wait up to 1ms for FlushLoop. Only frame 4 gets immediate drain.

With the upper bound removed:
- Frames 1-3 now try TryCommitAndDrain (non-blocking)
- In single-channel: FlushLoop is sleeping, locks are free, TryCommitAndDrain succeeds
- In multi-channel: only one caller succeeds (non-blocking), others fall back to FlushLoop

## Why It Should Help

- 1ch×1MB: all 16 frames of 64KB get non-blocking drain vs only 4/16 currently
- 1ch×100KB: first frame (64KB) now gets drain instead of waiting for FlushLoop
- TryCommitAndDrain uses Monitor.TryEnter + SemaphoreSlim.Wait(0) — zero contention added
- Game-tick unaffected: 64/256 byte messages are below 8KB threshold

## Success Criteria

- Bulk throughput ratios improve, especially 1ch and 10ch scenarios
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
