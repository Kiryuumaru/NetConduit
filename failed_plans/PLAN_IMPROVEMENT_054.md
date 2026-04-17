# Plan 054: Lower TryCommitAndDrain threshold from 8192 to 1024

## Change

Lower the TryCommitAndDrain bypass threshold in `WriteChannel.cs` from 8192 to 1024,
so that 1KB data frames get the same inline non-blocking drain as larger frames.

## Files Modified

- `src/NetConduit/WriteChannel.cs` — TryCommitAndDrain condition

## Analysis

### The Problem

In the benchmarks, 1KB throughput scenarios show the worst NC vs Go ratios:

| Scenario | NC | FRP | NC/FRP |
|----------|---:|----:|-------:|
| 1ch×1KB | 1.4 MB/s | 8.2 MB/s | 0.17x |
| 10ch×1KB | 15.5 MB/s | 19.6 MB/s | 0.79x |
| 100ch×1KB | 4.3 MB/s | 23.0 MB/s | 0.19x |

### Root Cause

The benchmark writes 1KB per channel. In `WriteChannel.WriteAsync`, `toSend = 1024`.
The bypass conditions check:

```csharp
if (toSend >= 65536 && accumulated >= 262144)  // ForceFlush
else if (toSend >= 8192)                         // TryCommitAndDrain
```

1024 < 8192 → **misses both bypasses**. Data sits in the Pipe until FlushLoop
wakes (1ms timer). This adds ~500-1000µs latency per transfer.

Go multiplexers (Yamux, Smux) have no timer delay — frames are sent directly
to the socket via a dedicated sender goroutine:
- Yamux: `sendCh` → `sendLoop()` → `conn.Write()` (immediate)
- Smux: `shaper` → `shaperLoop` → `sendLoop()` → `WriteBuffers()` (immediate)

### Why TryCommitAndDrain is Safe Here

TryCommitAndDrain is fully non-blocking:
1. `Monitor.TryEnter(_writeLock)` — returns false if contended, no wait
2. `_streamLock.Wait(0)` — returns false if held, no wait
3. If either fails, the method returns immediately and FlushLoop handles the drain

This is fundamentally different from Plan 043 (ForceFlush for ≥512B, FAILED)
which used blocking `ForceFlush` and caused contention in multi-channel scenarios.

### Game-Tick Safety

- 64B messages: toSend = 64, well below 1024 threshold
- 256B messages: toSend = 256, well below 1024 threshold
- No game-tick messages trigger TryCommitAndDrain under the new threshold

### Reference

- [hashicorp/yamux session.go](https://github.com/hashicorp/yamux/blob/master/session.go) — sendLoop writes directly to conn
- [xtaci/smux session.go](https://github.com/xtaci/smux/blob/master/session.go) — sendLoop uses WriteBuffers (writev)
- Failed Plan 043: ForceFlush (blocking) for ≥512B caused -78% at 100ch×1KB
- Succeeded Plan 053: Removed upper bound from TryCommitAndDrain (non-blocking)

## Success Criteria

- 1KB bulk throughput ratios improve vs FRP and Smux (target: closer to 1.0x)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B NC vs FRP does not drop more than 5% from baseline (10.68x)
- All tests pass
