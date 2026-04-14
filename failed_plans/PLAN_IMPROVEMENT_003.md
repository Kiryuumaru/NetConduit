# Plan 003: Auto-Flush at 64KB Pending — FAILED

**Addresses:** 1ms flush delay for bulk writes

**Change:** `src/NetConduit/StreamMultiplexer.cs` — In `SendFrameToWriter`, track `_pendingBytes`. When `_pendingBytes >= 65536`, call `SignalFlush()`. Reset in FlushLoop after drain.

**Theory:** Game-tick sends 64-256B messages (won't trigger). Bulk sends 64KB+ chunks (triggers immediate flush). Eliminates 0-1ms timer wait for large writes without hurting small message batching.

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| 1ch×100KB | 99.2 MB/s | ~99 MB/s | > 150 MB/s | FAIL |
| Game-tick 1ch×64B | 1,349,917 | ~1,350,000 | > 1,300,000 | Pass |

**Root cause:** No throughput improvement. FlushLoop was already running near-continuously for bulk transfers — the 1ms timer was not the bottleneck.
