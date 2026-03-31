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
| 1 | 1KB | 4.3 | 5.2 | 4.7 | 0.83x | 0.91x |
| 1 | 100KB | 307.0 | 298.3 | 354.3 | **1.03x** | 0.87x |
| 1 | 1MB | 363.7 | 719.1 | 864.7 | 0.51x | 0.42x |
| 10 | 1KB | 17.5 | 13.4 | 11.8 | **1.30x** | **1.48x** |
| 10 | 100KB | 568.1 | 427.4 | 642.1 | **1.33x** | 0.88x |
| 10 | 1MB | 777.5 | 784.3 | 1,095.8 | 0.99x | 0.71x |
| 100 | 1KB | 16.9 | 15.1 | 20.6 | **1.12x** | 0.82x |
| 100 | 100KB | 344.9 | 656.3 | 1,018.0 | 0.53x | 0.34x |
| 100 | 1MB | 436.5 | 1,114.9 | 1,773.1 | 0.39x | 0.25x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 104,657 | 89,650 | 115,080 | **1.17x** | 0.91x |
| 1 | 256B | 152,901 | 89,668 | 119,149 | **1.71x** | **1.28x** |
| 10 | 64B | 109,248 | 103,276 | 133,345 | **1.06x** | 0.82x |
| 10 | 256B | 123,467 | 105,446 | 131,248 | **1.17x** | 0.94x |
| 50 | 64B | 175,748 | 107,604 | 128,266 | **1.63x** | **1.37x** |
| 50 | 256B | 109,756 | 108,975 | 129,232 | **1.01x** | 0.85x |
| 1000 | 64B | 105,218 | 115,580 | 110,218 | 0.91x | 0.95x |
| 1000 | 256B | 96,030 | 129,711 | 110,750 | 0.74x | 0.87x |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 5/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.
NetConduit is competitive or faster for small-to-medium payloads (1KB–100KB).

**Game-tick messaging:** NetConduit wins 8/16 comparisons against Go multiplexers.
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
| 1 | 1KB | 4.1 | 5.2 | 4.7 | 2.6 | 4.3 |
| 1 | 100KB | 371.8 | 298.3 | 354.3 | 310.4 | 307.0 |
| 1 | 1MB | 1,488.1 | 719.1 | 864.7 | 1,309.9 | 363.7 |
| 10 | 1KB | 9.5 | 13.4 | 11.8 | 8.7 | 17.5 |
| 10 | 100KB | 715.7 | 427.4 | 642.1 | 725.0 | 568.1 |
| 10 | 1MB | 2,094.7 | 784.3 | 1,095.8 | 2,249.7 | 777.5 |
| 100 | 1KB | 8.2 | 15.1 | 20.6 | 11.5 | 16.9 |
| 100 | 100KB | 894.2 | 656.3 | 1,018.0 | 575.1 | 344.9 |
| 100 | 1MB | 2,974.4 | 1,114.9 | 1,773.1 | 1,358.9 | 436.5 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 216,112 | 89,650 | 115,080 | 1,538,185 | 104,657 |
| 1 | 256B | 225,770 | 89,668 | 119,149 | 1,338,398 | 152,901 |
| 10 | 64B | 1,509,248 | 103,276 | 133,345 | 1,502,401 | 109,248 |
| 10 | 256B | 1,528,034 | 105,446 | 131,248 | 1,747,146 | 123,467 |
| 50 | 64B | 1,645,648 | 107,604 | 128,266 | 2,166,247 | 175,748 |
| 50 | 256B | 1,581,831 | 108,975 | 129,232 | — | 109,756 |
| 1000 | 64B | 1,662,106 | 115,580 | 110,218 | 2,452,346 | 105,218 |
| 1000 | 256B | 1,516,754 | 129,711 | 110,750 | 2,168,164 | 96,030 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.6x | 0.8x | 0.9x |
| 1 | 100KB | 1.0x | 1.2x | 1.0x |
| 1 | 1MB | 3.6x | 2.1x | 1.7x |
| 10 | 1KB | 0.5x | 0.7x | 0.8x |
| 10 | 100KB | 1.3x | 1.7x | 1.1x |
| 10 | 1MB | 2.9x | 2.7x | 1.9x |
| 100 | 1KB | 0.7x | 0.5x | 0.4x |
| 100 | 100KB | 1.7x | 1.4x | 0.9x |
| 100 | 1MB | 3.1x | 2.7x | 1.7x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 14.7x | 2.4x | 1.9x |
| 1 | 256B | 8.8x | 2.5x | 1.9x |
| 10 | 64B | 13.8x | 14.6x | 11.3x |
| 10 | 256B | 14.2x | 14.5x | 11.6x |
| 50 | 64B | 12.3x | 15.3x | 12.8x |
| 50 | 256B | — | 14.5x | 12.2x |
| 1000 | 64B | 23.3x | 14.4x | 15.1x |
| 1000 | 256B | 22.6x | 11.7x | 13.7x |

---

*Generated by NetConduit comparison benchmark suite*
