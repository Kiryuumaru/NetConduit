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
| 027 | Reduce FlushInterval 1ms→250µs | Parameter | NC Mux crashes with InvalidOperationException (1ch×64B) and TimeoutException (1ch×256B); 250µs timer creates excessive FlushLoop wakeups causing contention/crash on 2-core pinned system; same crash pattern as Plan 028 |
| 028 | Remove frame-size guard from ForceFlush + reduce FlushInterval to 250µs | Structural | All NetConduit mux benchmarks throw InvalidOperationException/TimeoutException; removing toSend>=65536 guard causes ForceFlush from small-message writers, creating contention between caller-side drain and FlushLoop that corrupts pipe state; cascading failures broke even Raw TCP tests |
| 029 | ForceFlush on channel close | Structural | Bulk throughput regressed severely: 1ch×1KB -50%, 1ch×1MB -36%, 10ch×100KB -64%, 100ch×1KB -83%; ForceFlush in CloseAsync contends with FlushLoop (both compete for _writeLock and _streamLock); game-tick 1000ch×256B also regressed -42% |
| 030 | Lower ForceFlush accumulation threshold 256KB→128KB | Parameter | 1ch×1MB regressed 0.91x→0.40x (more frequent, smaller drains hurt single-channel); 100ch×100KB improved 0.22x→0.42x but gains don't offset single-channel losses; high inter-session variance complicates evaluation |
| 031 | Lower per-frame ForceFlush guard 64KB→16KB | Parameter | Mixed bulk ratios: 100ch×100KB improved 0.22x→0.51x, 1ch×100KB improved 0.27x→0.36x, but 10ch×100KB regressed 0.76x→0.31x; unchanged scenarios (1ch×1KB, 1ch×1MB) showed 26-33% variance confirming cross-session noise dominates; game-tick strong (20.14x 1ch×64B) |
| 032 | Reduce FlushInterval 1ms→500µs | Parameter | Regressions across board on 2-core pinning; shorter timer steals CPU from write/drain paths |
| 033 | Reduce FlushInterval 1ms→750µs | Parameter | Same regression pattern as 032; timer overhead too high on 2 cores |
| 034 | Adaptive FlushInterval (500µs–2ms based on throughput) | Structural | CPU waste from spinning on 2 cores; adaptation logic adds overhead |
| 035 | Reduce FlushInterval 1ms→250µs | Parameter | Worst timer reduction variant; same fundamental issue as 032-034 |
| 036 | Spin-wait in FlushLoop instead of timer | Structural | Burns one full core on 2-core system; throughput collapses |
| 037 | Signal-based flush on write completion | Structural | 1ch×1MB regression; signaling adds scheduling overhead on 2 cores |
| 038 | Hybrid signal+timer flush | Structural | Same regression as 037; signal overhead dominates |
| 039 | Conditional signal flush (large writes only) | Structural | Same regression as 037-038; any signaling hurts on 2 cores |
| 040 | Pipe segment coalescing (minimumSegmentSize 512KB) | Structural | Game-tick excellent but bulk catastrophic; large segments hurt TCP flow control and cause write stalls |
| 041 | Non-blocking drain in ForceFlush (fallback to SignalFlush) | Structural | Game-tick improved (+6-29%) but bulk 1ch×1MB -37% (0.57x vs 0.91x); SignalFlush fallback adds scheduling overhead; FlushLoop competes for _streamLock even in single-channel |
| 042 | Non-blocking drain in FlushLoop (inverse of 041) | Structural | Game-tick improved (+1-42%), 1000ch×64B now works (was FAILED). Bulk 1ch×1MB -58% (0.38x vs 0.91x); skipping FlushLoop drain breaks pipeline overlap; worse than 041 for single-channel |
| 043 | Post-write ForceFlush for writes >= 512B | Structural | 1ch x 1KB +137% but 100ch x 1KB -78%, 1ch x 100KB -37%; ForceFlush on every >= 512B write destroys FlushLoop batching for multi-channel |
| 044 | Post-write ForceFlush for small solo writes (16KB cap, solo-writer guard) | Structural | 1ch x 1KB -46% (0.13x vs 0.24x), 10ch x 1KB -64%; solo-writer detection via _unflushedDataBytes insufficient; ForceFlush contends with FlushLoop |
| 045 | Signal flush on empty-to-non-empty pipe transition | Structural | 1ch x 1MB -60% (0.36x vs 0.91x); signal fires after every FlushLoop commit; rapid small drains destroy batch efficiency |
| 046 | ForceFlush on first write to empty pipe for bulk frames | Structural | ForceFlush contention with FlushLoop; bulk 1ch x 1MB -45%; detecting empty pipe unreliable under concurrent writes |
| 047 | CAS idle-to-active state for FlushLoop fast-path | Structural | CAS transitions create scheduling overhead on 2 cores; FlushLoop state machine adds complexity without reducing latency |
| 048 | Conditional signal flush with solo-writer + payload threshold | Structural | Solo-writer detection via _activeWriterCount unreliable; signal contention on 2-core pinning; 100ch x 1KB -86% |
| 049 | ArrayPool drain coalescing in DrainPipeToStreamAsync | Structural | Extra buffer copy overhead exceeds TCP segment coalescing benefit; 1ch x 1MB -56%; ArrayPool contention under high concurrency |
| 050 | Inline writer-side non-blocking drain via TryInlineDrainAsync | Structural | _writeLock contention between writer inline CommitPipeWriter and FlushLoop; 1ch x 1MB -56%; 100ch x 1KB -80% |
| 051 | Solo-writer ForceFlush for medium frames (1024-65535B) via atomic counter | Structural | 1ch x 1KB -50% (0.12x vs 0.24x); 100ch x 1KB -88% (0.13x vs 1.10x); 1ch x 1MB -45% unexpectedly; Interlocked counter contention; ForceFlush overhead cancels timer elimination benefit |
| 052 | Inline commit + non-blocking TryDrainAsync | Unconditional CommitPipeWriter per-frame causes double-commit for large frames, -54% 1ch×1MB |
