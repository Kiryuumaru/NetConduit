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
