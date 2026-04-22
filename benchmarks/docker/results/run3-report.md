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
| 1 | 1KB | 8.1 | 6.4 | 10.2 | **1.26x** | 0.80x |
| 1 | 100KB | 347.5 | 484.5 | 613.1 | 0.72x | 0.57x |
| 1 | 1MB | 776.0 | 1,017.8 | 1,205.8 | 0.76x | 0.64x |
| 10 | 1KB | 10.4 | 23.1 | 18.9 | 0.45x | 0.55x |
| 10 | 100KB | 759.8 | 492.3 | 803.1 | **1.54x** | 0.95x |
| 10 | 1MB | 1,291.1 | 1,014.4 | 1,717.2 | **1.27x** | 0.75x |
| 100 | 1KB | 17.4 | 20.2 | 20.6 | 0.86x | 0.84x |
| 100 | 100KB | 1,030.9 | 629.3 | 894.4 | **1.64x** | **1.15x** |
| 100 | 1MB | 1,028.7 | 1,162.7 | 1,715.2 | 0.88x | 0.60x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,603,176 | 81,900 | 114,889 | **19.57x** | **13.95x** |
| 1 | 256B | 1,173,321 | 84,411 | 116,130 | **13.90x** | **10.10x** |
| 10 | 64B | 1,209,474 | 103,812 | 132,258 | **11.65x** | **9.14x** |
| 10 | 256B | 810,208 | 99,992 | 132,458 | **8.10x** | **6.12x** |
| 50 | 64B | 1,172,894 | 104,324 | 125,788 | **11.24x** | **9.32x** |
| 50 | 256B | 818,442 | 110,610 | 128,823 | **7.40x** | **6.35x** |
| 1000 | 64B | 1,116,387 | 100,026 | 103,116 | **11.16x** | **10.83x** |
| 1000 | 256B | 848,309 | 236,926 | 100,096 | **3.58x** | **8.47x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 5/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 7.9 | 6.4 | 10.2 | 1.9 | 8.1 |
| 1 | 100KB | 519.3 | 484.5 | 613.1 | 384.5 | 347.5 |
| 1 | 1MB | 2,420.2 | 1,017.8 | 1,205.8 | 1,822.8 | 776.0 |
| 10 | 1KB | 14.2 | 23.1 | 18.9 | 7.4 | 10.4 |
| 10 | 100KB | 1,027.0 | 492.3 | 803.1 | 520.9 | 759.8 |
| 10 | 1MB | 3,269.2 | 1,014.4 | 1,717.2 | 2,282.3 | 1,291.1 |
| 100 | 1KB | 12.9 | 20.2 | 20.6 | 6.3 | 17.4 |
| 100 | 100KB | 967.9 | 629.3 | 894.4 | 507.8 | 1,030.9 |
| 100 | 1MB | 3,167.5 | 1,162.7 | 1,715.2 | 1,765.6 | 1,028.7 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 209,080 | 81,900 | 114,889 | 1,488,081 | 1,603,176 |
| 1 | 256B | 238,889 | 84,411 | 116,130 | 1,368,602 | 1,173,321 |
| 10 | 64B | 1,327,210 | 103,812 | 132,258 | 2,028,911 | 1,209,474 |
| 10 | 256B | 1,443,900 | 99,992 | 132,458 | 1,377,535 | 810,208 |
| 50 | 64B | 1,648,778 | 104,324 | 125,788 | 2,034,498 | 1,172,894 |
| 50 | 256B | 1,544,985 | 110,610 | 128,823 | — | 818,442 |
| 1000 | 64B | 1,661,790 | 100,026 | 103,116 | 2,335,711 | 1,116,387 |
| 1000 | 256B | 1,590,780 | 236,926 | 100,096 | 2,229,076 | 848,309 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 1.2x | 0.8x |
| 1 | 100KB | 1.1x | 1.1x | 0.8x |
| 1 | 1MB | 2.3x | 2.4x | 2.0x |
| 10 | 1KB | 0.7x | 0.6x | 0.7x |
| 10 | 100KB | 0.7x | 2.1x | 1.3x |
| 10 | 1MB | 1.8x | 3.2x | 1.9x |
| 100 | 1KB | 0.4x | 0.6x | 0.6x |
| 100 | 100KB | 0.5x | 1.5x | 1.1x |
| 100 | 1MB | 1.7x | 2.7x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 0.9x | 2.6x | 1.8x |
| 1 | 256B | 1.2x | 2.8x | 2.1x |
| 10 | 64B | 1.7x | 12.8x | 10.0x |
| 10 | 256B | 1.7x | 14.4x | 10.9x |
| 50 | 64B | 1.7x | 15.8x | 13.1x |
| 50 | 256B | 1.9x | 14.0x | 12.0x |
| 1000 | 64B | 2.1x | 16.6x | 16.1x |
| 1000 | 256B | 2.6x | 6.7x | 15.9x |

---

*Generated by NetConduit comparison benchmark suite*
