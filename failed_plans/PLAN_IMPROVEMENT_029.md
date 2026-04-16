# Plan 029: ForceFlush on channel close to eliminate scheduling hop

## Change

Add `ForceFlushPipeToStreamAsync` to `WriteChannel.CloseAsync` so the FIN frame
(and any remaining buffered data) drains to the TCP stream on the caller's thread
instead of waiting for the FlushLoop task to be scheduled.

## Root Cause Analysis

For bulk throughput (1ch×1KB: 0.24x, 1ch×100KB: 0.27x), the bottleneck is:
1. Writer writes data frames to Pipe
2. Writer calls `CloseAsync` → `SendFin` → `SignalFlush`
3. **FlushLoop must be scheduled on a CPU core** (10-500µs on 2-core pinned system)
4. FlushLoop commits Pipe → drains to TCP stream
5. Reader receives data

Steps 3-4 add unnecessary latency. The writer thread is idle after CloseAsync
but the data sits in the Pipe until the FlushLoop task gets CPU time.

FRP/Yamux writes directly to the TCP socket — no Pipe intermediary, no scheduling hop.

## Why ForceFlush on Close Is Safe

- **One call per channel lifetime** — not per frame. No contention risk.
- **FlushLoop interaction**: ForceFlush acquires `_writeLock` → `_streamLock` (same
  order as FlushLoop). If both run, `_streamLock` serializes drains. Second drain
  finds empty Pipe and returns quickly.
- **SignalFlush still fires** from `SendFin`. If ForceFlush already drained,
  FlushLoop finds `_pendingFlush = false` and skips. Wastes one FlushLoop wake.
- **Game-tick unaffected**: channels are NOT closed during measurement window.
  `CloseAsync` only runs after the 2s timer expires (cleanup phase, not measured).

## Files Modified

- `src/NetConduit/WriteChannel.cs` — `CloseAsync` method

## What Changes

```csharp
// Before:
public ValueTask CloseAsync(CancellationToken cancellationToken = default)
{
    if (_state == ChannelState.Closed || _state == ChannelState.Closing)
        return default;
    _state = ChannelState.Closing;
    _multiplexer.SendFin(ChannelIndex, cancellationToken);
    return default;
}

// After:
public ValueTask CloseAsync(CancellationToken cancellationToken = default)
{
    if (_state == ChannelState.Closed || _state == ChannelState.Closing)
        return default;
    _state = ChannelState.Closing;
    _multiplexer.SendFin(ChannelIndex, cancellationToken);
    return _multiplexer.ForceFlushPipeToStreamAsync(cancellationToken);
}
```

## Success Criteria

- Bulk throughput ratios improve (especially 1KB and 100KB scenarios)
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
