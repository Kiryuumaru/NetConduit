# Plan 059 - Run 1 Results

## Game-Tick

| Config | NC (msg/s) | FRP (msg/s) | NC/FRP | Baseline NC/FRP | Delta |
|--------|-----------|------------|--------|-----------------|-------|
| 1ch×64B | 1,201,664 | 69,696 | 17.24x | 15.69x | +9.9% |
| 1ch×256B | 917,318 | 67,108 | 13.67x | 9.45x | +44.7% |
| 10ch×64B | 853,941 | 80,518 | 10.61x | 9.67x | +9.7% |
| 10ch×256B | 555,988 | 79,798 | 6.97x | 6.11x | +14.1% |
| 50ch×64B | 850,445 | 78,556 | 10.83x | 10.68x | +1.4% |
| 50ch×256B | 590,101 | 82,580 | 7.14x | 6.69x | +6.7% |
| 1000ch×64B | FAILED (TimeoutException) | 88,781 | FAILED | 10.79x | FAILED |
| 1000ch×256B | 602,119 | 90,612 | 6.64x | 4.58x | +45.0% |

## Throughput

| Config | NC (MB/s) | FRP (MB/s) | NC/FRP | Baseline NC/FRP | Delta |
|--------|----------|-----------|--------|-----------------|-------|
| 1ch×1KB | 0.8 | 7.6 | 0.11x | 0.17x | -35% |
| 1ch×100KB | 141.3 | 102.9 | 1.37x | 0.24x | +471% |
| 1ch×1MB | 390.0 | 352.7 | 1.11x | 0.74x | +50% |
| 10ch×1KB | 7.9 | 9.6 | 0.82x | 0.79x | +4% |
| 10ch×100KB | 165.0 | 208.6 | 0.79x | 0.70x | +13% |
| 10ch×1MB | 508.8 | 629.8 | 0.81x | 0.72x | +12.5% |
| 100ch×1KB | 10.7 | 16.8 | 0.64x | 0.19x | +237% |
| 100ch×100KB | 159.1 | 421.9 | 0.38x | 0.53x | -28% |
| 100ch×1MB | 569.8 | 897.3 | 0.63x | 0.55x | +15% |

## Notes
- Raw TCP (.NET) ch=50 msg=256 also FAILED (IOException) - system instability
- 1000ch×64B TimeoutException same as Plan 058 (different code) - likely system issue
- NC BEATS Go FRP at 1ch×100KB and 1ch×1MB!
