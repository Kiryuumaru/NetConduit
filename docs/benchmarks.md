# Benchmarks

All benchmarks: loopback TCP, `taskset 0x3` (2 CPU cores), GOMAXPROCS=2.
Each number is the median of 3 invocations (each invocation internally runs 5 iterations and takes its own median).
Competitors: [Yamux](https://github.com/hashicorp/yamux) (FRP), [Smux](https://github.com/xtaci/smux).

## Game-Tick Throughput (msg/s)

Small, frequent messages — typical of games, real-time sync, and RPC.

| Channels | Msg Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|----------|----------:|------:|-----:|---------:|--------:|
| 1 | 64B | 1,413,759 | 90,106 | 118,082 | **15.7x** | **12.0x** |
| 1 | 256B | 795,665 | 90,492 | 119,611 | **8.8x** | **6.7x** |
| 10 | 64B | 1,039,454 | 105,488 | 134,086 | **9.9x** | **7.8x** |
| 10 | 256B | 669,229 | 107,986 | 133,707 | **6.2x** | **5.0x** |
| 50 | 64B | 1,118,480 | 113,838 | 131,083 | **9.8x** | **8.5x** |
| 50 | 256B | 741,140 | 111,042 | 125,492 | **6.7x** | **5.9x** |
| 1000 | 64B | ~1,145,000 | 111,339 | 108,636 | **~10.3x** | **~10.5x** |
| 1000 | 256B | 782,434 | 290,950 | 109,222 | **2.7x** | **7.2x** |

**Win rate: 8/8 vs Yamux, 8/8 vs Smux.**

1000ch×64B: harness frequently hits socket TIME_WAIT across runs. Value from the successful run (confirmed consistent with deep-profile at ~1.2M msg/s).

## Bulk Throughput (MB/s)

Large sequential transfers — file copy, streaming, replication.

| Channels | Size | NetConduit | Yamux | Smux | vs Yamux | vs Smux |
|----------|------|----------:|------:|-----:|---------:|--------:|
| 1 | 1KB | 0.7 | 2.0 | 6.1 | 0.35x | 0.11x |
| 1 | 100KB | 106.3 | 306.0 | 377.6 | 0.35x | 0.28x |
| 1 | 1MB | 533.9 | 671.8 | 926.6 | 0.79x | 0.58x |
| 10 | 1KB | 7.6 | 14.4 | 13.7 | 0.53x | 0.55x |
| 10 | 100KB | 180.7 | 450.2 | 562.2 | 0.40x | 0.32x |
| 10 | 1MB | 700.0 | 866.9 | 1,089.5 | 0.81x | 0.64x |
| 100 | 1KB | 6.8 | 18.3 | 13.9 | 0.37x | 0.49x |
| 100 | 100KB | 284.3 | 654.7 | 979.8 | 0.43x | 0.29x |
| 100 | 1MB | 704.2 | 1,196.8 | 1,646.4 | 0.59x | 0.43x |

**Win rate: 0/9 vs Yamux, 0/9 vs Smux.**

Bulk throughput is the cost of NetConduit's credit-based flow control, adaptive windowing,
priority queuing, and reconnection support — features Yamux and Smux do not provide.
The 1KB scenarios are dominated by per-transfer setup latency rather than sustained throughput.

## Write Latency

| Channels | Avg | P50 | P90 | P99 |
|----------|----:|----:|----:|----:|
| 1 | 13.3 us | 11 us | 18 us | 39 us |
| 10 | 163.9 us | 153 us | 228 us | 468 us |
| 50 | 520.9 us | 413 us | 877 us | 3,130 us |
| 1000 (256B) | 14,092 us | 12,057 us | 23,624 us | 41,637 us |

## Test Environment

- **Transport:** Loopback TCP
- **CPU pinning:** `taskset 0x3` (2 cores)
- **Go muxes:** GOMAXPROCS=2
- **Method:** Median of 3 invocations × 5 internal iterations per invocation
- **Warmup:** Full channel setup completes before measurement starts
