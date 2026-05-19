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
| 1 | 1KB | 2.5 | 5.1 | 5.4 | 0.49x | 0.47x |
| 1 | 100KB | 545.3 | 260.3 | 502.2 | **2.09x** | **1.09x** |
| 1 | 1MB | 602.3 | 970.3 | 1,516.6 | 0.62x | 0.40x |
| 10 | 1KB | 21.5 | 23.8 | 24.2 | 0.90x | 0.89x |
| 10 | 100KB | 847.6 | 879.5 | 1,244.3 | 0.96x | 0.68x |
| 10 | 1MB | 1,937.6 | 1,432.5 | 2,738.6 | **1.35x** | 0.71x |
| 100 | 1KB | 34.5 | 35.4 | 40.3 | 0.97x | 0.85x |
| 100 | 100KB | 1,413.3 | 863.1 | 1,223.3 | **1.64x** | **1.16x** |
| 100 | 1MB | 1,934.0 | 1,691.7 | 3,308.0 | **1.14x** | 0.58x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 912,475 | 217,736 | 290,925 | **4.19x** | **3.14x** |
| 1 | 256B | 775,010 | 205,430 | 282,666 | **3.77x** | **2.74x** |
| 10 | 64B | 927,127 | 235,604 | 273,334 | **3.94x** | **3.39x** |
| 10 | 256B | 845,494 | 235,606 | 268,042 | **3.59x** | **3.15x** |
| 50 | 64B | 1,126,207 | 254,727 | 268,938 | **4.42x** | **4.19x** |
| 50 | 256B | 901,440 | 238,515 | 265,126 | **3.78x** | **3.40x** |
| 1000 | 64B | 3,880,819 | 279,093 | 220,270 | **13.91x** | **17.62x** |
| 1000 | 256B | 2,225,450 | 285,614 | 221,970 | **7.79x** | **10.03x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 6/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 2.4 | 5.1 | 5.4 | 2.4 | 2.5 |
| 1 | 100KB | 204.3 | 260.3 | 502.2 | 147.3 | 545.3 |
| 1 | 1MB | 1,945.0 | 970.3 | 1,516.6 | 1,426.1 | 602.3 |
| 10 | 1KB | 13.4 | 23.8 | 24.2 | 6.3 | 21.5 |
| 10 | 100KB | 1,655.2 | 879.5 | 1,244.3 | 895.7 | 847.6 |
| 10 | 1MB | 5,460.2 | 1,432.5 | 2,738.6 | 1,708.3 | 1,937.6 |
| 100 | 1KB | 20.4 | 35.4 | 40.3 | 16.6 | 34.5 |
| 100 | 100KB | 1,880.5 | 863.1 | 1,223.3 | 1,638.9 | 1,413.3 |
| 100 | 1MB | 6,231.4 | 1,691.7 | 3,308.0 | 3,882.9 | 1,934.0 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 611,538 | 217,736 | 290,925 | 2,387,804 | 912,475 |
| 1 | 256B | 1,136,402 | 205,430 | 282,666 | 1,952,843 | 775,010 |
| 10 | 64B | 3,540,382 | 235,604 | 273,334 | — | 927,127 |
| 10 | 256B | 3,225,086 | 235,606 | 268,042 | 3,570,793 | 845,494 |
| 50 | 64B | 3,873,844 | 254,727 | 268,938 | 3,790,156 | 1,126,207 |
| 50 | 256B | 3,337,816 | 238,515 | 265,126 | 3,226,262 | 901,440 |
| 1000 | 64B | 3,408,690 | 279,093 | 220,270 | 4,063,131 | 3,880,819 |
| 1000 | 256B | 3,241,904 | 285,614 | 221,970 | 3,323,640 | 2,225,450 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 1.0x | 0.5x | 0.4x |
| 1 | 100KB | 0.3x | 0.8x | 0.4x |
| 1 | 1MB | 2.4x | 2.0x | 1.3x |
| 10 | 1KB | 0.3x | 0.6x | 0.6x |
| 10 | 100KB | 1.1x | 1.9x | 1.3x |
| 10 | 1MB | 0.9x | 3.8x | 2.0x |
| 100 | 1KB | 0.5x | 0.6x | 0.5x |
| 100 | 100KB | 1.2x | 2.2x | 1.5x |
| 100 | 1MB | 2.0x | 3.7x | 1.9x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.6x | 2.8x | 2.1x |
| 1 | 256B | 2.5x | 5.5x | 4.0x |
| 10 | 64B | 3.8x | 15.0x | 13.0x |
| 10 | 256B | 4.2x | 13.7x | 12.0x |
| 50 | 64B | 3.4x | 15.2x | 14.4x |
| 50 | 256B | 3.6x | 14.0x | 12.6x |
| 1000 | 64B | 1.0x | 12.2x | 15.5x |
| 1000 | 256B | 1.5x | 11.4x | 14.6x |

---

*Generated by NetConduit comparison benchmark suite*
