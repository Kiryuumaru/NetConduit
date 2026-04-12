# Plan 002: Lower Credit Grant Threshold — FAILED

**Addresses:** Credit starvation

**Change:** `src/NetConduit/Internal/AdaptiveFlowControl.cs` — In `RecordConsumptionAndGetGrant`, change `windowSize / 4` to `windowSize / 8`.

**Theory:** Credits granted after consuming 1MB (1/4 of 4MB window). Lowering to 512KB (1/8) means grants twice as often, halving average stall wait. Trade-off: 2× more 9-byte credit frames (negligible bandwidth).

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| 1ch×100KB | 99.2 MB/s | ~99 MB/s | > 120 MB/s | FAIL |
| Game-tick 1ch×64B | 1,349,917 | ~1,350,000 | > 1,300,000 | Pass |

**Root cause:** More grants sent, but same FlushLoop RTT delivers them. Grant frequency is not the bottleneck.
