# Succeeded Plans Index

| # | Plan | Category | Key Result |
|---|------|----------|------------|
| 013 | Caller-side immediate drain for large frames | Structural | 1ch×1MB +40%, 10ch×100KB +41%, game-tick unaffected |
| 016 | Increase read PipeReader buffer (16KB→1MB) | Parameter | 1ch×1MB: 0.64x→1.16x (+81% vs FRP), 10ch×100KB: +24%/+55%, game-tick stable |
| 017 | Accumulation-based flush threshold (256KB) | Structural | 10ch×100KB: 0.33x→1.24x (+276% vs FRP), 1ch×1MB: +58%, game-tick stable |
| 023 | Direct delivery on read path (bypass per-channel Pipe) | Structural | 1ch×1MB: 0.50x→1.08x (+116% vs FRP, now BEATS Yamux), all 1ch variants +42-58%, game-tick within thresholds |
