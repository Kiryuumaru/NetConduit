# NetConduit Comparison Benchmark Results

All benchmarks run in Docker containers on loopback (127.0.0.1), identical workloads,
5 runs with median reported. CPU-pinned via `--cpuset-cpus=0,1` (2 cores).

**Fairness controls:** Both Go and .NET containers pinned to same 2 CPU cores,
GOMAXPROCS=2, `--network=none` (loopback only), identical Docker isolation.

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
| 1 | 1KB | 10.9 | 9.5 | 7.0 | **1.15x** | **1.56x** |
| 1 | 100KB | 671.2 | 454.2 | 849.5 | **1.48x** | 0.79x |
| 1 | 1MB | 761.2 | 1,522.9 | 3,057.3 | 0.50x | 0.25x |
| 10 | 1KB | 51.4 | 29.6 | 28.1 | **1.74x** | **1.83x** |
| 10 | 100KB | 992.0 | 800.6 | 1,382.2 | **1.24x** | 0.72x |
| 10 | 1MB | 1,485.6 | 2,012.3 | 3,177.2 | 0.74x | 0.47x |
| 100 | 1KB | 22.8 | 46.7 | 35.1 | 0.49x | 0.65x |
| 100 | 100KB | 1,238.9 | 1,123.6 | 1,766.3 | **1.10x** | 0.70x |
| 100 | 1MB | 1,883.1 | 2,039.0 | 3,510.1 | 0.92x | 0.54x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 936,046 | 220,680 | 279,494 | **4.24x** | **3.35x** |
| 1 | 256B | 737,605 | 163,328 | 101,138 | **4.52x** | **7.29x** |
| 10 | 64B | 981,674 | 212,436 | 237,996 | **4.62x** | **4.12x** |
| 10 | 256B | 803,669 | 211,589 | 247,240 | **3.80x** | **3.25x** |
| 50 | 64B | 1,248,971 | 209,048 | 234,070 | **5.97x** | **5.34x** |
| 50 | 256B | 824,112 | 227,098 | 244,260 | **3.63x** | **3.37x** |
| 1000 | 64B | 4,006,673 | 260,094 | 202,546 | **15.40x** | **19.78x** |
| 1000 | 256B | 2,306,928 | 269,388 | 199,039 | **8.56x** | **11.59x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 7/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 4.3 | 9.5 | 7.0 | 2.2 | 10.9 |
| 1 | 100KB | 459.1 | 454.2 | 849.5 | 326.1 | 671.2 |
| 1 | 1MB | 3,228.3 | 1,522.9 | 3,057.3 | 2,252.8 | 761.2 |
| 10 | 1KB | 21.0 | 29.6 | 28.1 | 10.1 | 51.4 |
| 10 | 100KB | 2,031.3 | 800.6 | 1,382.2 | 692.3 | 992.0 |
| 10 | 1MB | 7,346.4 | 2,012.3 | 3,177.2 | 2,353.3 | 1,485.6 |
| 100 | 1KB | 25.8 | 46.7 | 35.1 | 21.1 | 22.8 |
| 100 | 100KB | 2,104.1 | 1,123.6 | 1,766.3 | 1,712.6 | 1,238.9 |
| 100 | 1MB | 8,630.7 | 2,039.0 | 3,510.1 | 5,224.5 | 1,883.1 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 1,128,062 | 220,680 | 279,494 | 2,301,465 | 936,046 |
| 1 | 256B | 1,085,120 | 163,328 | 101,138 | 2,073,741 | 737,605 |
| 10 | 64B | 3,242,878 | 212,436 | 237,996 | — | 981,674 |
| 10 | 256B | 2,993,095 | 211,589 | 247,240 | 3,700,492 | 803,669 |
| 50 | 64B | 3,565,668 | 209,048 | 234,070 | 3,829,744 | 1,248,971 |
| 50 | 256B | 3,045,192 | 227,098 | 244,260 | 3,105,706 | 824,112 |
| 1000 | 64B | 3,339,752 | 260,094 | 202,546 | 4,620,606 | 4,006,673 |
| 1000 | 256B | 3,221,861 | 269,388 | 199,039 | 3,567,263 | 2,306,928 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 0.5x | 0.6x |
| 1 | 100KB | 0.5x | 1.0x | 0.5x |
| 1 | 1MB | 3.0x | 2.1x | 1.1x |
| 10 | 1KB | 0.2x | 0.7x | 0.7x |
| 10 | 100KB | 0.7x | 2.5x | 1.5x |
| 10 | 1MB | 1.6x | 3.7x | 2.3x |
| 100 | 1KB | 0.9x | 0.6x | 0.7x |
| 100 | 100KB | 1.4x | 1.9x | 1.2x |
| 100 | 1MB | 2.8x | 4.2x | 2.5x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.5x | 5.1x | 4.0x |
| 1 | 256B | 2.8x | 6.6x | 10.7x |
| 10 | 64B | 3.3x | 15.3x | 13.6x |
| 10 | 256B | 4.6x | 14.1x | 12.1x |
| 50 | 64B | 3.1x | 17.1x | 15.2x |
| 50 | 256B | 3.8x | 13.4x | 12.5x |
| 1000 | 64B | 1.2x | 12.8x | 16.5x |
| 1000 | 256B | 1.5x | 12.0x | 16.2x |

---

*Generated by NetConduit comparison benchmark suite*
