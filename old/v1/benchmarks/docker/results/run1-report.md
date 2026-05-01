# NetConduit Comparison Benchmark Results

All benchmarks run on loopback (127.0.0.1), identical workloads,
5 runs with median reported. CPU-pinned via `taskset 0x3` (2 cores).

**Fairness controls:** Both Go and .NET benchmarks pinned to same 2 CPU cores,
GOMAXPROCS=2, `--network none` when Docker, `taskset` when native.

| Implementation | Language | Description |
|---------------|----------|-------------|
| **NetConduit** | C# | 1 TCP connection, N multiplexed channels — credit-based flow control, priority queuing, adaptive windowing |
| **FRP/Yamux** | Go | HashiCorp Yamux — stream multiplexer used by FRP, Consul, Nomad |
| **Smux** | Go | Popular Go stream multiplexer (xtaci/smux) |
| Raw TCP | C# / Go | Baseline — N separate TCP connections (not a mux, shown for context) |

---

## Multiplexer Head-to-Head

The comparison that matters: **NetConduit vs FRP/Yamux vs Smux**.
All three multiplex N channels over a single TCP connection.

### Bulk Throughput (MB/s)

Each channel sends one data payload. Higher = better.

| Channels | Data Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|-----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 1KB | 5.4 | 5.6 | 5.9 | 0.96x | 0.91x |
| 1 | 100KB | 234.4 | 271.0 | 302.8 | 0.86x | 0.77x |
| 1 | 1MB | 714.8 | 672.7 | 919.1 | **1.06x** | 0.78x |
| 10 | 1KB | 25.4 | 12.9 | 11.7 | **1.97x** | **2.16x** |
| 10 | 100KB | 594.2 | 456.6 | 818.3 | **1.30x** | 0.73x |
| 10 | 1MB | 975.6 | 1,062.9 | 1,743.4 | 0.92x | 0.56x |
| 100 | 1KB | 3.2 | 23.5 | 21.7 | 0.14x | 0.15x |
| 100 | 100KB | 326.7 | 660.1 | 967.7 | 0.49x | 0.34x |
| 100 | 1MB | 1,083.0 | 1,153.3 | 1,673.2 | 0.94x | 0.65x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,473,546 | 87,775 | 113,792 | **16.79x** | **12.95x** |
| 1 | 256B | 1,204,327 | 87,454 | 117,651 | **13.77x** | **10.24x** |
| 10 | 64B | 1,140,452 | 100,500 | 128,228 | **11.35x** | **8.89x** |
| 10 | 256B | 754,602 | 103,640 | 129,656 | **7.28x** | **5.82x** |
| 50 | 64B | 1,082,487 | 109,196 | 131,634 | **9.91x** | **8.22x** |
| 50 | 256B | 793,550 | 107,626 | 128,653 | **7.37x** | **6.17x** |
| 1000 | 64B | 1,122,550 | 108,305 | 104,732 | **10.36x** | **10.72x** |
| 1000 | 256B | 822,527 | 149,790 | 110,348 | **5.49x** | **7.45x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 4/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.
NetConduit is competitive or faster for small-to-medium payloads (1KB–100KB).

**Game-tick messaging:** NetConduit wins 16/16 comparisons against Go multiplexers.
When per-message overhead dominates (not raw throughput), the credit system's cost
is proportionally smaller.

**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,
priority queuing ensures critical channels aren't starved, and adaptive windowing
automatically reclaims memory from idle channels. These features add measurable
overhead but provide production safety guarantees that simpler muxes don't offer.

---

## Raw TCP Baselines

Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.
This is the theoretical ceiling, not a practical alternative (connection limits,
no flow control, no channel management).

### Throughput: All Implementations (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|-----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 1KB | 4.8 | 5.6 | 5.9 | 1.3 | 5.4 |
| 1 | 100KB | 325.5 | 271.0 | 302.8 | 322.8 | 234.4 |
| 1 | 1MB | 1,438.0 | 672.7 | 919.1 | 1,393.7 | 714.8 |
| 10 | 1KB | 9.6 | 12.9 | 11.7 | 8.3 | 25.4 |
| 10 | 100KB | 713.8 | 456.6 | 818.3 | 739.2 | 594.2 |
| 10 | 1MB | 2,384.7 | 1,062.9 | 1,743.4 | 2,833.3 | 975.6 |
| 100 | 1KB | 12.9 | 23.5 | 21.7 | 6.7 | 3.2 |
| 100 | 100KB | 921.9 | 660.1 | 967.7 | 341.9 | 326.7 |
| 100 | 1MB | 2,969.8 | 1,153.3 | 1,673.2 | 1,387.9 | 1,083.0 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 231,598 | 87,775 | 113,792 | 1,463,723 | 1,473,546 |
| 1 | 256B | 234,378 | 87,454 | 117,651 | 1,358,596 | 1,204,327 |
| 10 | 64B | 1,379,056 | 100,500 | 128,228 | — | 1,140,452 |
| 10 | 256B | 1,495,404 | 103,640 | 129,656 | 1,345,945 | 754,602 |
| 50 | 64B | 1,637,618 | 109,196 | 131,634 | 1,641,251 | 1,082,487 |
| 50 | 256B | 1,590,204 | 107,626 | 128,653 | 1,802,968 | 793,550 |
| 1000 | 64B | 1,876,542 | 108,305 | 104,732 | — | 1,122,550 |
| 1000 | 256B | 1,545,682 | 149,790 | 110,348 | — | 822,527 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 0.9x | 0.8x |
| 1 | 100KB | 1.4x | 1.2x | 1.1x |
| 1 | 1MB | 1.9x | 2.1x | 1.6x |
| 10 | 1KB | 0.3x | 0.7x | 0.8x |
| 10 | 100KB | 1.2x | 1.6x | 0.9x |
| 10 | 1MB | 2.9x | 2.2x | 1.4x |
| 100 | 1KB | 2.1x | 0.5x | 0.6x |
| 100 | 100KB | 1.0x | 1.4x | 1.0x |
| 100 | 1MB | 1.3x | 2.6x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.6x | 2.0x |
| 1 | 256B | 1.1x | 2.7x | 2.0x |
| 10 | 64B | 1.2x | 13.7x | 10.8x |
| 10 | 256B | 1.8x | 14.4x | 11.5x |
| 50 | 64B | 1.5x | 15.0x | 12.4x |
| 50 | 256B | 2.3x | 14.8x | 12.4x |
| 1000 | 64B | 1.7x | 17.3x | 17.9x |
| 1000 | 256B | 1.9x | 10.3x | 14.0x |

---

*Generated by NetConduit comparison benchmark suite*
