# Plan 001: Signal Flush Before Credit Stall — FAILED

**Addresses:** Credit starvation + flush delay

**Change:** `src/NetConduit/WriteChannel.cs` — In `WriteAsync`, call `_multiplexer.SignalFlush()` before the credit wait when `credits <= 0`.

**Theory:** When sender runs out of credits, unflushed data sits in the pipe buffer. Signaling flush before blocking gets data to the receiver faster, so the credit grant returns sooner.

**Why safe for game-tick:** Only fires when credits are exhausted — never during game-tick where credits are plentiful (4MB window, 64B messages).

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| Credit wait | 3972 ms | ~3900 ms | < 3000 ms | FAIL |
| 1ch×100KB | 99.2 MB/s | ~99 MB/s | > 120 MB/s | FAIL |
| Game-tick 1ch×64B | 1,349,917 | ~1,350,000 | > 1,300,000 | Pass |

**Root cause:** Negligible impact on credit wait. FlushLoop was already running near-continuously.
