# Plan 022: Increase Accumulation Flush Threshold from 256KB to 1MB

**Addresses:** Write path lock contention — too many ForceFlush calls per credit window

**Change:** `src/NetConduit/WriteChannel.cs` — Change ForceFlush accumulation threshold from `262144` (256KB) to `1048576` (1MB).

**Theory:** With 256KB threshold and 4MB credit window, the sender invokes ForceFlush 16 times per window, each acquiring `_streamLock` and performing a TCP write. With 1MB threshold, only 4 ForceFlush calls per window — 75% fewer lock acquisitions and syscalls. Each TCP write is 4x larger, amortizing kernel overhead. Game-tick messages never trigger ForceFlush (toSend < 65536), so game-tick ratios are unaffected.

**Files modified:** `src/NetConduit/WriteChannel.cs` (1 line)

**Success criteria:**
- All tests pass
- Bulk throughput ratios improve (NC vs FRP or NC vs Smux closer to 1.0x)
- Game-tick ratios do not regress (NC vs FRP stays above 10x for 1ch×64B, 50ch×64B does not drop > 5%)
