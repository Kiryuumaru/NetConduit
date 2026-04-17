# Plan 047: Signal FlushLoop Only on Idle-to-Active Transition (CAS)

## Status: FAILED

## Hypothesis
By signaling the FlushLoop only when it transitions from idle to active (using CAS on an `_flushLoopIdle` flag), we avoid the O(N) signal overhead of per-write signaling while still getting low-latency wake-up for the first write after idle.

## Changes
- Added `private int _flushLoopIdle` field to StreamMultiplexer
- In SendFrameToWriter: `Interlocked.CompareExchange(ref _flushLoopIdle, 0, 1) == 1` → SignalFlush
- In FlushLoopAsync: set _flushLoopIdle=1 before timed wait, check _pendingFlush before entering wait, set _flushLoopIdle=0 after wake

## Results

### Game-tick (NC vs FRP ratio)
| Test | Plan 047 | Baseline | Delta |
|------|----------|----------|-------|
| 1ch×64B | 17.24x | 17.49x | -1.4% |
| 1ch×256B | 10.83x | 10.37x | +4.4% |
| 50ch×64B | 10.18x | 9.73x | +4.6% |
| 50ch×256B | 6.77x | 6.77x | same |
| 1000ch×64B | FAILED (TimeoutException) | 4.49x | BROKEN |
| 1000ch×256B | FAILED (TimeoutException) | 4.49x | BROKEN |

### Bulk throughput (NC vs FRP ratio)
| Test | Plan 047 | Baseline | Delta |
|------|----------|----------|-------|
| 1ch×1KB | 0.17x | 0.24x | -29% |
| 1ch×100KB | 0.14x | 0.27x | -48% |
| 1ch×1MB | 0.58x | 0.91x | -36% |
| 10ch×1KB | 0.19x | 0.25x | -24% |
| 10ch×100KB | 0.71x | 0.76x | -7% |
| 10ch×1MB | 0.74x | 0.85x | -13% |
| 100ch×1KB | 0.64x | 1.10x | -42% |
| 100ch×100KB | 0.33x | 0.22x | +50% |
| 100ch×1MB | 0.62x | 0.63x | same |

## Verdict: FAIL
- 1000ch game-tick completely broken (TimeoutException)
- Bulk throughput regressed heavily across the board
- CAS approach likely causes FlushLoop starvation at high channel counts
