# Plan 024: Signal FlushLoop for medium+ data frames

## Change

In `SendFrameToWriter`, signal the FlushLoop when the data payload size is >= 1024 bytes.
Currently, only `FlushMode.Immediate` or high-priority channels signal the FlushLoop.
Normal data frames in batched mode rely on the 1ms timer, adding unnecessary latency for
medium-to-large payloads that don't hit the ForceFlush threshold.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — `SendFrameToWriter` method

## Why It Should Help

For payloads like 1KB or 100KB, the sender writes frames that are:
- Too small for ForceFlush (requires toSend >= 65536 AND accumulated >= 262144)
- Not signaled to FlushLoop (batched mode doesn't signal for normal data)

The data sits in the Pipe for up to 1ms (FlushLoop timer interval).
For 1ch×100KB, this 1ms dominates the transfer time (100KB takes <<1ms on loopback).

By signaling the FlushLoop for frames with payload >= 1024 bytes, medium-to-large payloads
get drained in microseconds instead of milliseconds.

Game-tick messages (64B, 256B) remain below the threshold and continue to batch efficiently.

## What Changes

```csharp
// Before:
_pendingFlush = true;
if (_options.FlushMode == FlushMode.Immediate || forceFlush)
    SignalFlush();

// After:
_pendingFlush = true;
if (_options.FlushMode == FlushMode.Immediate || forceFlush || payload.Length >= 1024)
    SignalFlush();
```

## Success Criteria

- Bulk throughput ratios improve, especially 100KB scenarios (currently 0.23-0.43x vs FRP)
- Game-tick ratios do not regress (1ch×64B NC vs FRP stays above 10x, 50ch×64B within 5%)
- All tests pass
