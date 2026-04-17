# Succeeded Plans Index

| # | Plan | Category | Key Result |
|---|------|----------|------------|
| 013 | Caller-side immediate drain for large frames | Structural | 1ch×1MB +40%, 10ch×100KB +41%, game-tick unaffected |
| 016 | Increase read PipeReader buffer (16KB→1MB) | Parameter | 1ch×1MB: 0.64x→1.16x (+81% vs FRP), 10ch×100KB: +24%/+55%, game-tick stable |
| 017 | Accumulation-based flush threshold (256KB) | Structural | 10ch×100KB: 0.33x→1.24x (+276% vs FRP), 1ch×1MB: +58%, game-tick stable |
| 023 | Direct delivery on read path (bypass per-channel Pipe) | Structural | 1ch×1MB: 0.50x→1.08x (+116% vs FRP, now BEATS Yamux), all 1ch variants +42-58%, game-tick within thresholds |
| 053 | Remove upper bound from TryCommitAndDrain condition | Parameter | 1ch×1MB: 0.91x→1.09x (+20%), 10ch×100KB: 0.76x→1.23x (+62%, beats FRP), 10ch×1MB: 0.85x→1.08x (+27%), 100ch×1MB: 0.63x→0.73x (+16%), game-tick stable (1ch×64B 16.75x, 50ch×64B 10.54x) |
| 068 | Fix benchmark reconnection deadlock + larger Pipe segments (65KB) | Structural + Harness | 100ch×1MB: 0.55x→1.21x (beats FRP), 10ch×100KB: 0.70x→1.13x, game-tick 1000ch stable (11.35x vs FRP), benchmark 1000ch timeout eliminated |
