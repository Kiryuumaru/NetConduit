# Plan 008: Grant-on-Receive + SignalFlush (with BP Check) — FAILED

**Addresses:** Credit starvation (eliminate read delay from credit cycle)

**Change:** `src/NetConduit/ReadChannel.cs` — Grant credits in `EnqueueData` on receive. Added `_unreadBytes` tracking with 2× MaxCredits backpressure threshold. Deferred grants released in ConsumeBuffer when unread exceeds threshold. Called `SignalFlush` from EnqueueData.

**Theory:** App read delay adds latency to credit cycle. Grant on receive eliminates this. BP check prevents unbounded buffering.

| Metric | Baseline | Result (median) | Target | Verdict |
|--------|----------|-----------------|--------|---------|
| Credit wait | 3972 ms | 3958 ms | < 2000 ms | FAIL |
| Starvations | 80,873 | 80,964 | < 20,000 | FAIL |
| 1ch×100KB | 99.2 MB/s | 93.2 MB/s | > 150 MB/s | FAIL |
| 1ch×1MB | 315.9 MB/s | 410.9 MB/s | > 500 MB/s | FAIL |
| Game-tick 1ch×64B | 1,349,917 | 1,286,034 | > 1,200,000 | Pass (-5%) |
| Game-tick 50ch×64B | 1,141,737 | 1,042,259 | no regress | FAIL (-9%) |

**Root cause:** BP check negated the change (receiver buffers exceed 8MB almost immediately, causing all grants to be deferred to ConsumeBuffer — same as baseline). SignalFlush on every receive hurts game-tick batching.
