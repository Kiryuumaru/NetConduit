# Plan 010: Idle-Triggered Flush — FAILED

**Addresses:** 1ms flush delay (eliminate timer wait for first frame after idle)

**Change:** `src/NetConduit/StreamMultiplexer.cs` — In `SendFrameToWriter`, detect when `_pendingFlush` was false (first frame after FlushLoop cleared it). Signal flush immediately on idle→active transition. Subsequent frames ride the existing flush cycle.

**Theory:** 0-1ms FlushLoop timer delay is the throughput bottleneck for single-shot transfers. Idle-triggered flush eliminates this delay for the first frame in a batch.

| Metric | Baseline | Result (median) | Verdict |
|--------|----------|-----------------|---------|
| 1ch×1KB | 2.4 MB/s | 3.8 MB/s | +58% (tiny only) |
| 1ch×100KB | 99.2 MB/s | 99.6 MB/s | Flat |
| 1ch×1MB | 315.9 MB/s | 327.9 MB/s | +4% (noise) |
| 10ch×1MB | 549.3 MB/s | 490.0 MB/s | -11% regression |
| Game-tick 1ch×64B | 1,349,917 | 1,236,839 | **-8% regression** |
| Game-tick 50ch×64B | 1,141,737 | 1,045,112 | **-8% regression** |

**Root cause:** FlushLoop timer is not the throughput bottleneck for 100KB+ messages. The overhead is the multiplexer pipeline itself: 3 memory copies + 2 pipe synchronizations. More frequent wakeups create smaller batches, reducing throughput and hurting game-tick.
