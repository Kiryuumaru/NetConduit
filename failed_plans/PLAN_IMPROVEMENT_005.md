# Plan 005: Eager Grant in EnqueueData via FlushLoop — FAILED

**Addresses:** Credit starvation (eliminate read delay from credit cycle)

**Change:** `src/NetConduit/ReadChannel.cs` — Move `RecordConsumptionAndGetGrant` from `ConsumeBuffer` to `EnqueueData`. Grant credits on receive, not on consume. Use `EnqueuePendingCredit` + existing FlushLoop path.

**Theory:** Currently credits wait for app to read data before granting. If app reads slowly, sender stalls unnecessarily. Grant on receive eliminates this delay.

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| Credit starvations | 80,873 | ~80,000 | < 10,000 | FAIL |
| Credit wait | 3972 ms | ~3950 ms | < 1000 ms | FAIL |
| 1ch×100KB | 99.2 MB/s | ~99 MB/s | > 200 MB/s | FAIL |

**Root cause:** Grants still go through FlushLoop. The FlushLoop RTT dominates, not when the grant decision is made.
