# Plan 007: Direct-to-Stream Credit Grants — FAILED

**Addresses:** Credit starvation (bypass FlushLoop for credits)

**Change:** Write 18-byte credit frame directly to `_writeStream` under `_streamLock` via `SendCreditGrantDirectAsync`. Fire-and-forget from ConsumeBuffer, bypassing FlushLoop entirely.

**Theory:** Credits are tiny (18 bytes) but queued behind large data batches in FlushLoop. Writing directly to stream skips the queue.

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| Credit wait | 3972 ms | 3906 ms | < 2000 ms | FAIL |
| 1ch×100KB | 99.2 MB/s | ~96.5 MB/s | > 150 MB/s | FAIL |
| Credit starvations | 74,479 | 55,892-76,133 | < 10,000 | FAIL |
| Game-tick 1ch×64B | 1,349,917 | ~1,300,117 | > 1,200,000 | Pass |

**Root cause:** `_streamLock` contention. `DrainPipeToStreamAsync` holds the lock for ~475us avg × 6,537 cycles = 3.1s out of 5s wall time. Credit grants queue behind data drains waiting for the same TCP stream lock.
