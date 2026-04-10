# Benchmarks

All benchmarks: loopback TCP, `taskset 0x3` (2 CPU cores), GOMAXPROCS=2, 5 runs with median reported.
Competitors: [Yamux](https://github.com/hashicorp/yamux) (FRP), [Smux](https://github.com/xtaci/smux).

## Game-Tick Throughput (msg/s)

Small, frequent messages — typical of games, real-time sync, and RPC.

| Channels | Msg Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|----------|----------:|------:|-----:|---------:|--------:|
| 1 | 64B | 1,465,101 | 76,966 | 103,518 | **19.0x** | **14.2x** |
| 1 | 256B | 825,129 | 77,432 | 105,194 | **10.7x** | **7.8x** |
| 10 | 64B | 927,570 | 94,132 | 115,042 | **9.9x** | **8.1x** |
| 10 | 256B | 628,590 | 92,192 | 113,068 | **6.8x** | **5.6x** |
| 50 | 64B | 1,013,510 | 97,592 | 108,187 | **10.4x** | **9.4x** |
| 50 | 256B | 681,576 | 93,234 | 105,194 | **7.3x** | **6.5x** |
| 1000 | 256B | 665,284 | 106,667 | 88,604 | **6.2x** | **7.5x** |

**Win rate: 7/7 vs Yamux, 7/7 vs Smux.**

1000ch×64B excluded — harness frequently hits TimeoutException from socket TIME_WAIT accumulation across runs.

## Bulk Throughput (MB/s)

Large sequential transfers — file copy, streaming, replication.

| Channels | Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|------|----------:|------:|-----:|---------:|--------:|
| 1 | 1KB | 3.8 | 8.7 | 8.7 | 0.43x | 0.43x |
| 1 | 100KB | 101.1 | 357.6 | 451.1 | 0.28x | 0.22x |
| 1 | 1MB | 674.6 | 931.6 | 1,249.8 | 0.72x | 0.54x |
| 10 | 1KB | 9.9 | 18.1 | 18.0 | 0.55x | 0.55x |
| 10 | 100KB | 317.2 | 447.4 | 765.7 | 0.71x | 0.41x |
| 10 | 1MB | 596.9 | 888.9 | 1,364.8 | 0.67x | 0.44x |
| 100 | 1KB | 5.8 | 21.0 | 15.2 | 0.27x | 0.38x |
| 100 | 100KB | 225.6 | 564.6 | 838.3 | 0.40x | 0.27x |
| 100 | 1MB | 898.3 | 1,059.5 | 1,472.6 | 0.85x | 0.61x |

**Win rate: 0/9 vs Yamux, 0/9 vs Smux.**

Bulk throughput is the cost of NetConduit's credit-based flow control, adaptive windowing,
priority queuing, and reconnection support — features Yamux and Smux do not provide.
The 1KB scenarios are dominated by per-transfer setup latency rather than sustained throughput.

## Test Environment

- **Transport:** Loopback TCP
- **CPU pinning:** `taskset 0x3` (2 cores)
- **Go muxes:** GOMAXPROCS=2
- **Method:** 5 runs, median reported
- **Warmup:** Full channel setup completes before measurement starts
