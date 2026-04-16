# Plan 060 - Run 1 Results

## Game-Tick (msg/s)

### Go
| Config | Raw TCP | FRP/Yamux | Smux |
|--------|---------|-----------|------|
| ch=1, msg=64 | 171,846 | 61,373 | 85,832 |
| ch=1, msg=256 | 190,366 | 59,177 | 84,752 |
| ch=10, msg=64 | 912,858 | 75,622 | 85,440 |
| ch=10, msg=256 | 1,156,980 | 73,982 | 83,528 |
| ch=50, msg=64 | 1,296,868 | 78,556 | 80,328 |
| ch=50, msg=256 | 1,239,844 | 73,326 | 76,719 |
| ch=1000, msg=64 | 1,632,698 | 86,878 | 60,104 |
| ch=1000, msg=256 | 1,493,944 | 86,928 | 60,838 |

### .NET
| Config | Raw TCP | NetConduit |
|--------|---------|------------|
| ch=1, msg=64 | 741,361 | 1,194,407 |
| ch=1, msg=256 | 724,004 | 900,126 |
| ch=10, msg=64 | 1,537,244 | 830,425 |
| ch=10, msg=256 | IOException | 579,542 |
| ch=50, msg=64 | 1,832,749 | 864,242 |
| ch=50, msg=256 | 770,773 | 608,853 |
| ch=1000, msg=64 | 2,047,845 | 1,034,558 |
| ch=1000, msg=256 | IOException | TimeoutException |

### NC/FRP Game-Tick Ratios
| Config | Run 1 | Docs | Baseline Today |
|--------|-------|------|----------------|
| 1ch×64B | 19.46x | 15.69x | 20.96x |
| 1ch×256B | 15.21x | 9.45x | — |
| 10ch×64B | 10.98x | 9.67x | 10.88x |
| 10ch×256B | 7.83x | 6.11x | — |
| 50ch×64B | 11.00x | 10.68x | 10.63x |
| 50ch×256B | 8.30x | 6.69x | — |
| 1000ch×64B | 11.91x | 10.79x | 10.06x |
| 1000ch×256B | TIMEOUT | 4.58x | TIMEOUT |

## Throughput (MB/s)

### Go
| Config | Raw TCP | FRP/Yamux | Smux |
|--------|---------|-----------|------|
| 1ch×1KB | 4.3 | 4.6 | 4.1 |
| 1ch×100KB | 322.7 | 154.2 | 372.7 |
| 1ch×1MB | 1,124.0 | 720.0 | 1,153.5 |
| 10ch×1KB | 10.8 | 15.4 | 13.0 |
| 10ch×100KB | 618.2 | 385.4 | 552.5 |
| 10ch×1MB | 2,049.7 | 686.3 | 1,135.0 |
| 100ch×1KB | 7.1 | 15.1 | 16.7 |
| 100ch×100KB | 545.2 | 489.5 | 671.3 |
| 100ch×1MB | 2,175.6 | 787.7 | 1,031.0 |

### .NET
| Config | Raw TCP | NetConduit |
|--------|---------|------------|
| 1ch×1KB | 2.5 | 0.8 |
| 1ch×100KB | 179.1 | 63.1 |
| 1ch×1MB | 1,093.0 | 251.8 |
| 10ch×1KB | 5.9 | 3.0 |
| 10ch×100KB | 397.4 | 229.3 |
| 10ch×1MB | 1,370.2 | 403.8 |
| 100ch×1KB | 6.6 | 4.1 |
| 100ch×100KB | 367.4 | 187.3 |
| 100ch×1MB | 1,229.1 | 642.5 |

### NC/FRP Throughput Ratios
| Config | Run 1 | Docs | Baseline Today |
|--------|-------|------|----------------|
| 1ch×1KB | 0.17x | 0.17x | 0.11x |
| 1ch×100KB | 0.41x | 0.24x | 0.29x |
| 1ch×1MB | 0.35x | 0.74x | 0.81x |
| 10ch×1KB | 0.19x | 0.79x | 0.37x |
| 10ch×100KB | 0.59x | 0.70x | 0.40x |
| 10ch×1MB | 0.59x | 0.72x | 0.58x |
| 100ch×1KB | 0.27x | 0.19x | 0.24x |
| 100ch×100KB | 0.38x | 0.53x | 0.51x |
| 100ch×1MB | 0.82x | 0.55x | 0.72x |

## Analysis

### Game-Tick: ALL PASS
- 1ch×64B: 19.46x (>10x ✅, docs 15.69x ✅)
- 50ch×64B: 11.00x (within 5% of docs 10.68x ✅)
- 1000ch×64B: 11.91x (docs 10.79x ✅, NO TIMEOUT ✅)
- All non-timeout scenarios improved vs docs

### Throughput: MIXED
- vs Baseline: 5 improved, 1 same, 3 regressed
- Major regressions: 1ch×1MB (0.81→0.35x), 10ch×1KB (0.37→0.19x)
- Major improvements: 1ch×100KB (0.29→0.41x), 10ch×100KB (0.40→0.59x), 100ch×1MB (0.72→0.82x)
- System highly variable (Go FRP 1ch×1MB: baseline 605, run1 720 = +19% swing)
