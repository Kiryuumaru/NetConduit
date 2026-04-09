# Plan 004: Increase Default MaxCredits — FAILED

**Addresses:** Credit starvation

**Change:** `src/NetConduit/Models/ChannelOptions.cs` + `DefaultChannelOptions.cs` — Change `MaxCredits` from `4 * 1024 * 1024` to `8 * 1024 * 1024`.

**Theory:** With 4MB window, sender exhausts credits after ~62 messages of 64KB. Doubling to 8MB gives ~124 messages before first stall. More data flows before blocking.

| Metric | Baseline | Result | Target | Verdict |
|--------|----------|--------|--------|---------|
| 1ch×100KB | 99.2 MB/s | ~99 MB/s | > 120 MB/s | FAIL |
| 1ch×1MB | 315.9 MB/s | ~316 MB/s | > 400 MB/s | FAIL |
| Game-tick 1ch×64B | 1,349,917 | ~1,350,000 | > 1,300,000 | Pass |

**Root cause:** More headroom delays the first stall but same RTT governs steady-state throughput. Window size is not the bottleneck.
