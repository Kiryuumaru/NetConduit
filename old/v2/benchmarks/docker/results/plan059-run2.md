# Plan 059 - Run 2 Results

## Game-Tick

| Config | NC (msg/s) | FRP (msg/s) | NC/FRP | Baseline NC/FRP | Delta |
|--------|-----------|------------|--------|-----------------|-------|
| 1ch×64B | 1,225,957 | 62,852 | 19.51x | 15.69x | +24.3% |
| 1ch×256B | 901,163 | 62,010 | 14.53x | 9.45x | +53.8% |
| 10ch×64B | 785,971 | 79,425 | 9.90x | 9.67x | +2.4% |
| 10ch×256B | 543,150 | 75,172 | 7.23x | 6.11x | +18.3% |
| 50ch×64B | 859,362 | 77,974 | 11.02x | 10.68x | +3.2% |
| 50ch×256B | 572,905 | 78,114 | 7.33x | 6.69x | +9.6% |
| 1000ch×64B | 847,990 | 96,378 | 8.80x | 10.79x | -18.4% REGRESSED |
| 1000ch×256B | 572,405 | 89,102 | 6.42x | 4.58x | +40.2% |

## Throughput

| Config | NC (MB/s) | FRP (MB/s) | NC/FRP | Baseline NC/FRP | Delta |
|--------|----------|-----------|--------|-----------------|-------|
| 1ch×1KB | 0.8 | 6.1 | 0.13x | 0.17x | -24% |
| 1ch×100KB | 64.3 | 287.9 | 0.22x | 0.24x | -8% |
| 1ch×1MB | 273.1 | 661.8 | 0.41x | 0.74x | -45% |
| 10ch×1KB | 2.7 | 17.9 | 0.15x | 0.79x | -81% |
| 10ch×100KB | 168.8 | 340.2 | 0.50x | 0.70x | -29% |
| 10ch×1MB | 384.0 | 695.8 | 0.55x | 0.72x | -24% |
| 100ch×1KB | 4.7 | 14.8 | 0.32x | 0.19x | +68% |
| 100ch×100KB | 191.8 | 496.6 | 0.39x | 0.53x | -26% |
| 100ch×1MB | 523.6 | 810.1 | 0.65x | 0.55x | +18% |

## Notes
- System EXTREMELY unstable: Raw TCP (.NET) IOExceptions at ch=10 msg=256 AND ch=1000 msg=256
- Go FRP numbers wildly different from run 1 (1ch×100KB: 102.9→287.9 MB/s, 2.8x swing)
- NC absolute throughput much lower than run 1 across most scenarios
- Run is effectively invalid due to system instability
