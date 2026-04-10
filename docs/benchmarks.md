# Benchmarks

All benchmarks: loopback TCP, `taskset 0x3` (2 CPU cores), GOMAXPROCS=2, 5 runs with median reported.
Competitors: [Yamux](https://github.com/hashicorp/yamux) (FRP), [Smux](https://github.com/xtaci/smux).
Game-tick and bulk categories run interleaved (Go+.NET back-to-back per category) to minimize environmental variance.

## Game-Tick Throughput (msg/s)

Small, frequent messages — typical of games, real-time sync, and RPC.

| Channels | Msg Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|----------|----------:|------:|-----:|---------:|--------:|
| 1 | 64B | 1,595,094 | 88,800 | 120,804 | **18.0x** | **13.2x** |
| 1 | 256B | 906,600 | 89,682 | 122,971 | **10.1x** | **7.4x** |
| 10 | 64B | 1,009,572 | 106,987 | 137,678 | **9.4x** | **7.3x** |
| 10 | 256B | 599,130 | 112,572 | 139,886 | **5.3x** | **4.3x** |
| 50 | 64B | 1,023,906 | 113,431 | 131,212 | **9.0x** | **7.8x** |
| 50 | 256B | 695,455 | 108,838 | 128,929 | **6.4x** | **5.4x** |
| 1000 | 64B | 1,015,726 | 108,097 | 107,790 | **9.4x** | **9.4x** |
| 1000 | 256B | 621,376 | 120,829 | 106,086 | **5.1x** | **5.9x** |

**Win rate: 8/8 vs Yamux, 8/8 vs Smux.**

## Bulk Throughput (MB/s)

Large sequential transfers — file copy, streaming, replication.

| Channels | Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|------|----------:|------:|-----:|---------:|--------:|
| 1 | 1KB | 1.4 | 6.5 | 9.1 | 0.22x | 0.16x |
| 1 | 100KB | 104.1 | 345.6 | 552.5 | 0.30x | 0.19x |
| 1 | 1MB | 470.5 | 1,103.1 | 1,137.5 | 0.43x | 0.41x |
| 10 | 1KB | 11.1 | 16.9 | 19.9 | 0.66x | 0.56x |
| 10 | 100KB | 172.6 | 625.9 | 723.0 | 0.28x | 0.24x |
| 10 | 1MB | 521.6 | 974.7 | 1,505.3 | 0.54x | 0.35x |
| 100 | 1KB | 3.3 | 24.0 | 20.2 | 0.14x | 0.16x |
| 100 | 100KB | 315.0 | 620.0 | 1,014.3 | 0.51x | 0.31x |
| 100 | 1MB | 794.1 | 1,210.4 | 1,525.2 | 0.66x | 0.52x |

**Win rate: 0/9 vs Yamux, 0/9 vs Smux.**

Bulk throughput is the cost of NetConduit's credit-based flow control, adaptive windowing,
priority queuing, and reconnection support — features Yamux and Smux do not provide.
The 1KB scenarios are dominated by per-transfer setup latency rather than sustained throughput.

## Test Environment

- **Transport:** Loopback TCP
- **CPU pinning:** `taskset 0x3` (2 cores)
- **Go muxes:** GOMAXPROCS=2
- **Method:** 5 runs, median reported; categories interleaved (Go+.NET back-to-back)
- **Warmup:** Full channel setup completes before measurement starts
