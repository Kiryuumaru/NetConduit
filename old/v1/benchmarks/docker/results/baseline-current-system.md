# Baseline Benchmark - Current System State (Unmodified Code)

## System Observations
- Go FRP significantly degraded vs docs/benchmarks.md (-19% to -48%)
- .NET Raw TCP: 5/8 game-tick entries got IOException
- NC ch=1000 msg=256: TimeoutException (baseline code, no changes)
- System conditions NOT equivalent to docs/benchmarks.md measurement day

## Game-Tick Results

### Go Game-Tick
| Config | Raw TCP | FRP/Yamux | Smux |
|--------|---------|-----------|------|
| ch=1, msg=64 | 162,398 | 59,348 | 86,796 |
| ch=1, msg=256 | 174,746 | 60,540 | 85,573 |
| ch=10, msg=64 | 987,176 | 77,592 | 88,746 |
| ch=10, msg=256 | 1,177,720 | 77,201 | 87,483 |
| ch=50, msg=64 | 1,297,318 | 78,440 | 82,712 |
| ch=50, msg=256 | 1,260,044 | 77,196 | 83,708 |
| ch=1000, msg=64 | 1,348,315 | 90,890 | 63,394 |
| ch=1000, msg=256 | 1,371,688 | 85,710 | 63,946 |

### .NET Game-Tick
| Config | Raw TCP | NetConduit |
|--------|---------|------------|
| ch=1, msg=64 | IOException | 1,243,996 |
| ch=1, msg=256 | 741,000 | 714,552 |
| ch=10, msg=64 | 951,651 | 843,368 |
| ch=10, msg=256 | IOException | 491,068 |
| ch=50, msg=64 | IOException | 833,525 |
| ch=50, msg=256 | IOException | 526,818 |
| ch=1000, msg=64 | IOException | 914,331 |
| ch=1000, msg=256 | IOException | TimeoutException |

### NC/FRP Ratios
| Config | Today Ratio | docs/benchmarks.md Ratio |
|--------|-------------|--------------------------|
| 1ch×64B | 20.96x | 15.69x |
| 50ch×64B | 10.63x | 10.68x |
| 1000ch×64B | 10.06x | 10.79x |
| 1000ch×256B | TIMEOUT | 4.58x |

## Throughput Results

### Go Throughput
| Config | Raw TCP | FRP/Yamux | Smux |
|--------|---------|-----------|------|
| 1ch×1KB | 4.3 | 7.1 | 6.0 |
| 1ch×100KB | 268.0 | 190.9 | 295.4 |
| 1ch×1MB | 1,175.6 | 605.2 | 930.9 |
| 10ch×1KB | 8.7 | 14.5 | 11.7 |
| 10ch×100KB | 750.1 | 288.4 | 591.6 |
| 10ch×1MB | 1,652.3 | 710.9 | 989.3 |
| 100ch×1KB | 6.9 | 16.5 | 13.9 |
| 100ch×100KB | 563.6 | 369.8 | 566.2 |
| 100ch×1MB | 1,948.6 | 851.9 | 1,093.4 |

### .NET Throughput
| Config | Raw TCP | NetConduit |
|--------|---------|------------|
| 1ch×1KB | 3.1 | 0.8 |
| 1ch×100KB | 275.9 | 55.2 |
| 1ch×1MB | 1,002.3 | 493.0 |
| 10ch×1KB | 4.9 | 5.3 |
| 10ch×100KB | 386.8 | 116.3 |
| 10ch×1MB | 1,013.1 | 411.3 |
| 100ch×1KB | 6.8 | 4.0 |
| 100ch×100KB | 265.4 | 188.9 |
| 100ch×1MB | 1,258.1 | 611.1 |

### NC/FRP Throughput Ratios
| Config | Today Ratio | docs/benchmarks.md Ratio |
|--------|-------------|--------------------------|
| 1ch×1KB | 0.11x | 0.17x |
| 1ch×100KB | 0.29x | 0.24x |
| 1ch×1MB | 0.81x | 0.74x |
| 10ch×1KB | 0.37x | 0.79x |
| 10ch×100KB | 0.40x | 0.70x |
| 10ch×1MB | 0.58x | 0.72x |
| 100ch×1KB | 0.24x | 0.19x |
| 100ch×100KB | 0.51x | 0.53x |
| 100ch×1MB | 0.72x | 0.55x |
