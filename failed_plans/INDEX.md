# Failed Plans Index

| # | Plan | Category | Why It Failed |
|---|------|----------|---------------|
| 001 | SignalFlush before credit stall | Parameter | Negligible impact on credit wait |
| 002 | Lower grant threshold (1/4→1/8) | Parameter | More grants, same FlushLoop RTT |
| 003 | Auto-flush at 64KB pending | Parameter | No throughput improvement |
| 004 | Increase MaxCredits (4→8MB) | Parameter | More headroom, same RTT |
| 005 | Eager grant in EnqueueData (FlushLoop) | Structural | Grants still go through FlushLoop |
| 006 | Eliminate EnqueueData copy | Not attempted | Too complex, not the bottleneck |
| 007 | Direct-to-stream credits | Structural | `_streamLock` contention with data drains |
| 008 | Grant-on-receive + SignalFlush (BP check) | Structural | BP check negated change; game-tick -9% |
| 009 | Grant-on-receive (no BP check) | Structural | App reads fast — grant timing irrelevant |
| 010 | Idle-triggered flush | Structural | Not the bottleneck; game-tick -8% |
| 011 | Larger read buffer + adaptive flush + coalesce | Structural | Small-payload throughput regressed 3x; game-tick 50ch -12%; 1000ch failures |
| 012 | Pending-bytes flush + 32KB per-frame threshold | Parameter | Bulk throughput flat/neutral; 32KB threshold too conservative to matter |
| 014 | Accumulation-based flush threshold (256KB/128KB) | Structural | 1ch×1MB regressed -22% to -43%; SignalFlush scheduling delay hurts single-channel |
| 015 | Immediate credit grant flush on reader side | Structural | Reader ForceFlush contends with sender ForceFlush on _streamLock; 1ch×1MB -35% |
| 016 | Increase read PipeReader buffer (16KB→1MB) | Parameter | Re-evaluated: PASS — moved to succeeded_plans |
| 017 | Accumulation-based flush threshold (no SignalFlush fallback) | Structural | Re-evaluated: PASS — moved to succeeded_plans |
| 018 | Contention-adaptive flush (skip when _streamLock busy) | Structural | FlushLoop background contention makes CurrentCount unreliable; regression across all scenarios |
| 019 | Aggressive credit grant threshold (1/16 window) | Parameter | More credit frames add CPU overhead without reducing stall time; widespread regressions except 100ch×100KB |
| 020 | Direct-to-stream write for large frames | Structural | _streamLock contention with FlushLoop + two WriteAsync calls add more overhead than memcpy saves; single-channel -44% |
| 021 | Revert Plan 017 (keep 016 only) | Parameter | Per-frame ForceFlush adds more lock contention than accumulation delay; 1ch×100KB -58%, 10ch×100KB -45% |
| 022 | Increase accumulation threshold (256KB→1MB) | Parameter | Mixed results: 10ch×100KB +36% & 100ch×1MB +14% improved, but 10ch×1MB -9% & 100ch×100KB -15% regressed; no consistent gain |
| 024 | Signal FlushLoop for payload >= 1024 bytes | Parameter | 1ch×100KB +135% but 100ch×1KB -86% (per-frame signal threshold too low — overwhelms FlushLoop with many small-frame channels) |
| 025 | Signal FlushLoop on accumulated >= 65536 bytes | Parameter | 50ch×64B game-tick collapsed -85% (1.82x vs baseline 12.15x); signaling FlushLoop during active writes steals CPU and causes scheduling interference on 2-core pinning |
| 026 | Tiered ForceFlush with non-blocking try-drain at 65KB | Structural | Benchmark hung on 100ch tests; TryForceFlush holding _streamLock while acquiring _writeLock creates contention spiral; two concurrent lock orderings (_writeLock→_streamLock vs _streamLock→_writeLock) cause severe scheduling overhead |
