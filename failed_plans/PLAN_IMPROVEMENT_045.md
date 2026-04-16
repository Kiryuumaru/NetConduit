# Plan 045: Signal flush on empty-to-non-empty pipe transition

## Status: FAILED

## Change

In SendFrameToWriter (StreamMultiplexer.cs), call SignalFlush() when the write is the first data added to an empty pipe (_unflushedDataBytes == 0 before incrementing).

## Files Modified

- src/NetConduit/StreamMultiplexer.cs - SendFrameToWriter method (2-line change)

## Why It Should Help

Eliminates 1ms flush delay for the first write after each flush cycle. Unlike Plans 037-039 which signaled on EVERY write (O(N) signals), this signals only on empty-to-non-empty transitions (O(K) signals where K = FlushLoop cycles).

## Results

### Bulk Throughput (NC vs FRP)
| Scenario | Baseline | Plan 045 | Change |
|----------|----------|----------|--------|
| 1ch x 1KB | 0.24x | 0.14x | -42% |
| 1ch x 100KB | 0.27x | 0.20x | -26% |
| 1ch x 1MB | 0.91x | 0.36x | -60% |
| 10ch x 1KB | 0.25x | 0.38x | +52% |
| 10ch x 100KB | 0.76x | 0.35x | -54% |
| 10ch x 1MB | 0.85x | 0.62x | -27% |
| 100ch x 1KB | 1.10x | 0.23x | -79% |
| 100ch x 100KB | 0.22x | 0.50x | +127% |
| 100ch x 1MB | 0.63x | 0.57x | -10% |

### Game-tick: mostly stable, but 1000ch x 256B FAILED (TimeoutException)

## Why It Failed

Signal fires after every FlushLoop commit (because commit resets _unflushedDataBytes=0, making next write see empty pipe). For sustained/large transfers, FlushLoop cycles too frequently, breaking batch efficiency. 1ch x 1MB regressed from 0.91x to 0.36x. The pipe empties after each drain, triggering another signal, creating rapid small drains instead of efficient large batches. Same fundamental problem as Plans 037-039 but with lower signal frequency - still too many signals for sustained throughput.
