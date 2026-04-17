# Plan 042: Non-blocking _streamLock in FlushLoop (Inverse of Plan 041)

## Status: FAILED

## Hypothesis

Plan 041 made ForceFlush non-blocking (fallback to SignalFlush). Plan 042 makes FlushLoop non-blocking instead. When FlushLoop cannot acquire _streamLock, it simply SKIPS drain. No SignalFlush. ForceFlush keeps blocking drain.

## Change

In FlushLoopAsync, replaced `await DrainPipeToStreamAsync(ct)` with inline non-blocking drain using `_streamLock.Wait(0)`. If lock busy, skip drain entirely.

## Results

### Game-Tick — ALL IMPROVED
- 1ch×64B: 17.68x (baseline 17.49x, +1.1%)
- 1ch×256B: 10.95x (baseline 10.37x, +5.6%)
- 50ch×64B: 10.29x (baseline 9.73x, +5.8%)
- 50ch×256B: 7.04x (baseline 6.77x, +4.0%)
- 1000ch×64B: 10.47x (baseline FAILED — now works)
- 1000ch×256B: 6.38x (baseline 4.49x, +42%)

### Bulk Throughput — SEVERE REGRESSIONS
- 1ch×1KB: 0.14x (baseline 0.24x, -42%)
- 1ch×1MB: 0.38x (baseline 0.91x, -58%)
- 10ch×1KB: 0.95x (baseline 0.25x, +280%)
- 10ch×1MB: 0.66x (baseline 0.85x, -22%)
- 100ch×1KB: 0.16x (baseline 1.10x, -85%)
- 100ch×100KB: 0.43x (baseline 0.22x, +95%)
- 100ch×1MB: 0.63x (baseline 0.63x, 0%)

## Root Cause

FlushLoop drain provides critical pipeline overlap for single-channel bulk. When FlushLoop skips drain, pipeline becomes sequential (drain bandwidth halved). Both Plan 041 and 042 fail for same reason: reducing drain overlap hurts single-channel bulk.

## Key Insight

Two drain paths (FlushLoop + ForceFlush) working together provide higher effective drain bandwidth than either alone. Making either non-blocking breaks this overlap.
