# Plan 009: Grant-on-Receive Without Backpressure Check — FAILED

**Addresses:** Credit starvation (pure grant-on-receive theory)

**Change:** `src/NetConduit/ReadChannel.cs` — Moved `RecordConsumptionAndGetGrant` from ConsumeBuffer to EnqueueData. No backpressure threshold — grant unconditionally on every receive. Removed BP check that negated Plan 008.

**Theory:** Plan 008's BP check negated the change. Removing it tests the pure theory: does granting credits on receive (vs consume) help?

| Metric | Baseline | Result | Verdict |
|--------|----------|--------|---------|
| Credit wait | 3972 ms | 3923 ms | No change |
| Starvations | 80,873 | 53,459 | No real improvement |
| 1ch×100KB | 99.2 MB/s | ~97 MB/s | No change |
| 1ch×1MB | 315.9 MB/s | ~337 MB/s | Noise |
| Game-tick 1ch×64B | 1,349,917 | ~1,280,000 | Slight regression |

4 BackpressureTests failed (expected — unlimited credits means sender never blocks).

**Root cause:** Grant timing (receive vs consume) is irrelevant. App reads fast enough that both are nearly simultaneous. Credit round-trip time is dominated by FlushLoop + TCP + ReadLoop parsing, not when the grant decision is made.
