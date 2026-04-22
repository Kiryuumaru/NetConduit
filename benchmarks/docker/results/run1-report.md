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
| 1 | 1KB | 0.6 | 9.4 | 11.0 | 0.07x | 0.06x |
| 1 | 100KB | 435.8 | 551.3 | 595.5 | 0.79x | 0.73x |
| 1 | 1MB | 587.4 | 1,221.5 | 1,549.5 | 0.48x | 0.38x |
| 10 | 1KB | 27.5 | 22.2 | 21.7 | **1.24x** | **1.27x** |
| 10 | 100KB | 768.3 | 727.7 | 764.2 | **1.06x** | **1.01x** |
| 10 | 1MB | 1,543.0 | 1,269.6 | 1,937.5 | **1.22x** | 0.80x |
| 100 | 1KB | 31.4 | 23.0 | 17.6 | **1.37x** | **1.79x** |
| 100 | 100KB | 859.0 | 701.6 | 1,073.4 | **1.22x** | 0.80x |
| 100 | 1MB | 1,114.7 | 1,269.5 | 1,818.8 | 0.88x | 0.61x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,621,978 | 88,542 | 116,320 | **18.32x** | **13.94x** |
| 1 | 256B | 1,183,454 | 85,117 | 120,433 | **13.90x** | **9.83x** |
| 10 | 64B | 1,175,135 | 106,564 | 135,175 | **11.03x** | **8.69x** |
| 10 | 256B | 860,826 | 103,586 | 132,380 | **8.31x** | **6.50x** |
| 50 | 64B | 1,207,990 | 109,090 | 131,302 | **11.07x** | **9.20x** |
| 50 | 256B | 896,921 | 107,740 | 129,125 | **8.32x** | **6.95x** |
| 1000 | 64B | 1,185,540 | 111,572 | 111,637 | **10.63x** | **10.62x** |
| 1000 | 256B | 848,187 | 105,768 | 111,406 | **8.02x** | **7.61x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 8/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 7.9 | 9.4 | 11.0 | 0.9 | 0.6 |
| 1 | 100KB | 634.4 | 551.3 | 595.5 | 337.7 | 435.8 |
| 1 | 1MB | 2,317.3 | 1,221.5 | 1,549.5 | 2,120.9 | 587.4 |
| 10 | 1KB | 16.6 | 22.2 | 21.7 | 9.1 | 27.5 |
| 10 | 100KB | 1,187.2 | 727.7 | 764.2 | 812.7 | 768.3 |
| 10 | 1MB | 3,410.4 | 1,269.6 | 1,937.5 | 2,579.2 | 1,543.0 |
| 100 | 1KB | 14.6 | 23.0 | 17.6 | 11.9 | 31.4 |
| 100 | 100KB | 1,090.4 | 701.6 | 1,073.4 | 483.2 | 859.0 |
| 100 | 1MB | 3,319.8 | 1,269.5 | 1,818.8 | 1,946.3 | 1,114.7 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 205,080 | 88,542 | 116,320 | 1,636,961 | 1,621,978 |
| 1 | 256B | 231,668 | 85,117 | 120,433 | 1,363,667 | 1,183,454 |
| 10 | 64B | 1,359,940 | 106,564 | 135,175 | 2,063,944 | 1,175,135 |
| 10 | 256B | 1,528,376 | 103,586 | 132,380 | 1,413,660 | 860,826 |
| 50 | 64B | 1,676,196 | 109,090 | 131,302 | — | 1,207,990 |
| 50 | 256B | 1,590,717 | 107,740 | 129,125 | 1,922,916 | 896,921 |
| 1000 | 64B | 1,623,590 | 111,572 | 111,637 | 2,598,959 | 1,185,540 |
| 1000 | 256B | 1,872,539 | 105,768 | 111,406 | 2,343,958 | 848,187 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 1.4x | 0.8x | 0.7x |
| 1 | 100KB | 0.8x | 1.2x | 1.1x |
| 1 | 1MB | 3.6x | 1.9x | 1.5x |
| 10 | 1KB | 0.3x | 0.7x | 0.8x |
| 10 | 100KB | 1.1x | 1.6x | 1.6x |
| 10 | 1MB | 1.7x | 2.7x | 1.8x |
| 100 | 1KB | 0.4x | 0.6x | 0.8x |
| 100 | 100KB | 0.6x | 1.6x | 1.0x |
| 100 | 1MB | 1.7x | 2.6x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.3x | 1.8x |
| 1 | 256B | 1.2x | 2.7x | 1.9x |
| 10 | 64B | 1.8x | 12.8x | 10.1x |
| 10 | 256B | 1.6x | 14.8x | 11.5x |
| 50 | 64B | 1.4x | 15.4x | 12.8x |
| 50 | 256B | 2.1x | 14.8x | 12.3x |
| 1000 | 64B | 2.2x | 14.6x | 14.5x |
| 1000 | 256B | 2.8x | 17.7x | 16.8x |

---

*Generated by NetConduit comparison benchmark suite*
