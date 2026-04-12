# Plan 027: Reduce FlushInterval from 1ms to 250µs

## Change

Change the default `FlushInterval` from `TimeSpan.FromMilliseconds(1)` to
`TimeSpan.FromMicroseconds(250)`.

## Lesson From Plans 024-026

- Plan 024 (signal per frame): FlushLoop scheduling interference on multi-channel
- Plan 025 (signal on accumulated bytes): same FlushLoop interference
- Plan 026 (non-blocking TryForceFlush): reversed lock ordering causes contention spiral

All three failed because they introduced new mechanisms (signals or lock patterns).
The safest approach: no new mechanisms, just tune the existing timer.

## Files Modified

- `src/NetConduit/Models/MultiplexerOptions.cs` — `FlushInterval` default value

## Why It Should Help

For single-shot transfers like 1ch×100KB (100KB total data), the FlushLoop timer
is the dominant latency. Data sits in PipeWriter for 0-1ms (avg 500µs) before the
FlushLoop wakes and drains it. At 250µs interval, max wait drops to 250µs (avg 125µs).

This is pure timer tuning — no new code paths, no lock changes, no signal patterns.
The FlushLoop runs the same code 4x more often.

## Expected Impact

- 1ch×100KB: ~2x improvement (FlushLoop wait drops from ~500µs avg to ~125µs avg)
- Game-tick: FlushLoop wakes 4x more often but each cycle handles proportionally
  less data. CPU overhead increases from ~4% to ~16%. With 10-18x headroom over
  competitors, a ~10% game-tick regression is acceptable.
- Multi-channel: credit grant signals already wake FlushLoop between timer intervals,
  so the timer reduction has diminishing effect when credits flow actively.

## Success Criteria

- Bulk throughput ratios improve, especially 100KB scenarios
- Game-tick 1ch×64B NC vs FRP stays above 10x
- Game-tick 50ch×64B does not drop more than 5% from baseline
- All tests pass
