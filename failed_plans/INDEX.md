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
| 016 | Increase read PipeReader buffer (16KB→1MB) | Parameter | Cache pollution and alloc overhead; 1ch×1KB -68%, 1ch×1MB -24%, game-tick 1ch -17% |
| 017 | Accumulation-based flush threshold (no SignalFlush fallback) | Structural | Batching reduces sender-receiver pipelining; 1ch×1MB -14%; game-tick 1000ch TimeoutException |
| 018 | Contention-adaptive flush (skip when _streamLock busy) | Structural | FlushLoop background contention makes CurrentCount unreliable; regression across all scenarios |
