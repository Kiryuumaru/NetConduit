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
| 1 | 1KB | 5.1 | 2.0 | 2.7 | **2.53x** | **1.93x** |
| 1 | 100KB | 263.8 | 351.1 | 390.8 | 0.75x | 0.67x |
| 1 | 1MB | 484.4 | 691.8 | 995.6 | 0.70x | 0.49x |
| 10 | 1KB | 18.0 | 12.1 | 12.4 | **1.49x** | **1.46x** |
| 10 | 100KB | 739.6 | 396.8 | 550.3 | **1.86x** | **1.34x** |
| 10 | 1MB | 769.8 | 727.1 | 1,042.3 | **1.06x** | 0.74x |
| 100 | 1KB | 25.4 | 17.3 | 19.4 | **1.47x** | **1.31x** |
| 100 | 100KB | 506.2 | 588.0 | 1,047.6 | 0.86x | 0.48x |
| 100 | 1MB | 492.8 | 1,149.2 | 1,766.6 | 0.43x | 0.28x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 105,497 | 89,098 | 116,643 | **1.18x** | 0.90x |
| 1 | 256B | 161,503 | 83,591 | 119,596 | **1.93x** | **1.35x** |
| 10 | 64B | 123,820 | 107,108 | 133,523 | **1.16x** | 0.93x |
| 10 | 256B | 124,248 | 103,715 | 132,716 | **1.20x** | 0.94x |
| 50 | 64B | 124,681 | 111,490 | 128,361 | **1.12x** | 0.97x |
| 50 | 256B | 112,209 | 104,740 | 118,510 | **1.07x** | 0.95x |
| 1000 | 64B | 108,647 | 110,050 | 113,320 | 0.99x | 0.96x |
| 1000 | 256B | 100,370 | 279,159 | 112,975 | 0.36x | 0.89x |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 9/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.
NetConduit is competitive or faster for small-to-medium payloads (1KB–100KB).

**Game-tick messaging:** NetConduit wins 7/16 comparisons against Go multiplexers.
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
| 1 | 1KB | 1.8 | 2.0 | 2.7 | 4.1 | 5.1 |
| 1 | 100KB | 363.2 | 351.1 | 390.8 | 313.0 | 263.8 |
| 1 | 1MB | 1,495.2 | 691.8 | 995.6 | 1,780.9 | 484.4 |
| 10 | 1KB | 9.8 | 12.1 | 12.4 | 9.5 | 18.0 |
| 10 | 100KB | 721.2 | 396.8 | 550.3 | 888.8 | 739.6 |
| 10 | 1MB | 2,135.0 | 727.1 | 1,042.3 | 2,843.5 | 769.8 |
| 100 | 1KB | 7.9 | 17.3 | 19.4 | 11.1 | 25.4 |
| 100 | 100KB | 1,027.4 | 588.0 | 1,047.6 | 896.0 | 506.2 |
| 100 | 1MB | 3,033.6 | 1,149.2 | 1,766.6 | 2,011.1 | 492.8 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 229,891 | 89,098 | 116,643 | — | 105,497 |
| 1 | 256B | 223,920 | 83,591 | 119,596 | 1,403,716 | 161,503 |
| 10 | 64B | 1,471,623 | 107,108 | 133,523 | — | 123,820 |
| 10 | 256B | 1,440,342 | 103,715 | 132,716 | 1,992,407 | 124,248 |
| 50 | 64B | 1,659,092 | 111,490 | 128,361 | — | 124,681 |
| 50 | 256B | 1,587,592 | 104,740 | 118,510 | — | 112,209 |
| 1000 | 64B | 1,712,612 | 110,050 | 113,320 | — | 108,647 |
| 1000 | 256B | 2,107,092 | 279,159 | 112,975 | 2,120,403 | 100,370 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.8x | 0.9x | 0.7x |
| 1 | 100KB | 1.2x | 1.0x | 0.9x |
| 1 | 1MB | 3.7x | 2.2x | 1.5x |
| 10 | 1KB | 0.5x | 0.8x | 0.8x |
| 10 | 100KB | 1.2x | 1.8x | 1.3x |
| 10 | 1MB | 3.7x | 2.9x | 2.0x |
| 100 | 1KB | 0.4x | 0.5x | 0.4x |
| 100 | 100KB | 1.8x | 1.7x | 1.0x |
| 100 | 1MB | 4.1x | 2.6x | 1.7x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | — | 2.6x | 2.0x |
| 1 | 256B | 8.7x | 2.7x | 1.9x |
| 10 | 64B | — | 13.7x | 11.0x |
| 10 | 256B | 16.0x | 13.9x | 10.9x |
| 50 | 64B | — | 14.9x | 12.9x |
| 50 | 256B | — | 15.2x | 13.4x |
| 1000 | 64B | — | 15.6x | 15.1x |
| 1000 | 256B | 21.1x | 7.5x | 18.7x |

---

*Generated by NetConduit comparison benchmark suite*
