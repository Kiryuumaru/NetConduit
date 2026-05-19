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
| 1 | 1KB | 8.1 | 5.8 | 5.7 | **1.39x** | **1.42x** |
| 1 | 100KB | 846.2 | 347.7 | 597.3 | **2.43x** | **1.42x** |
| 1 | 1MB | 936.9 | 1,237.6 | 2,531.9 | 0.76x | 0.37x |
| 10 | 1KB | 67.1 | 31.3 | 28.5 | **2.14x** | **2.35x** |
| 10 | 100KB | 1,765.3 | 999.8 | 1,740.1 | **1.77x** | **1.01x** |
| 10 | 1MB | 2,111.7 | 2,054.7 | 2,782.9 | **1.03x** | 0.76x |
| 100 | 1KB | 32.0 | 44.3 | 36.0 | 0.72x | 0.89x |
| 100 | 100KB | 1,817.4 | 953.5 | 2,194.8 | **1.91x** | 0.83x |
| 100 | 1MB | 2,202.4 | 1,666.0 | 3,281.9 | **1.32x** | 0.67x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,045,049 | 232,560 | 302,226 | **4.49x** | **3.46x** |
| 1 | 256B | 974,350 | 197,790 | 291,039 | **4.93x** | **3.35x** |
| 10 | 64B | 1,086,380 | 232,750 | 264,775 | **4.67x** | **4.10x** |
| 10 | 256B | 966,529 | 215,370 | 270,266 | **4.49x** | **3.58x** |
| 50 | 64B | 1,292,230 | 228,572 | 271,642 | **5.65x** | **4.76x** |
| 50 | 256B | 1,016,322 | 223,994 | 269,955 | **4.54x** | **3.76x** |
| 1000 | 64B | 4,162,043 | 262,222 | 221,604 | **15.87x** | **18.78x** |
| 1000 | 256B | 2,469,060 | 435,684 | 226,352 | **5.67x** | **10.91x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 11/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 2.5 | 5.8 | 5.7 | 2.1 | 8.1 |
| 1 | 100KB | 327.7 | 347.7 | 597.3 | 335.1 | 846.2 |
| 1 | 1MB | 2,190.6 | 1,237.6 | 2,531.9 | 2,046.2 | 936.9 |
| 10 | 1KB | 21.1 | 31.3 | 28.5 | 13.2 | 67.1 |
| 10 | 100KB | 1,897.6 | 999.8 | 1,740.1 | 1,465.9 | 1,765.3 |
| 10 | 1MB | 6,168.1 | 2,054.7 | 2,782.9 | 2,943.9 | 2,111.7 |
| 100 | 1KB | 26.8 | 44.3 | 36.0 | 27.0 | 32.0 |
| 100 | 100KB | 1,934.6 | 953.5 | 2,194.8 | 1,809.1 | 1,817.4 |
| 100 | 1MB | 6,929.3 | 1,666.0 | 3,281.9 | 4,006.7 | 2,202.4 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 675,816 | 232,560 | 302,226 | — | 1,045,049 |
| 1 | 256B | 1,203,692 | 197,790 | 291,039 | 2,170,016 | 974,350 |
| 10 | 64B | 3,458,604 | 232,750 | 264,775 | — | 1,086,380 |
| 10 | 256B | 3,229,714 | 215,370 | 270,266 | — | 966,529 |
| 50 | 64B | 3,825,877 | 228,572 | 271,642 | 4,427,945 | 1,292,230 |
| 50 | 256B | 3,311,616 | 223,994 | 269,955 | 3,926,625 | 1,016,322 |
| 1000 | 64B | 3,656,580 | 262,222 | 221,604 | 5,046,077 | 4,162,043 |
| 1000 | 256B | 7,721,131 | 435,684 | 226,352 | 3,865,881 | 2,469,060 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.3x | 0.4x | 0.4x |
| 1 | 100KB | 0.4x | 0.9x | 0.5x |
| 1 | 1MB | 2.2x | 1.8x | 0.9x |
| 10 | 1KB | 0.2x | 0.7x | 0.7x |
| 10 | 100KB | 0.8x | 1.9x | 1.1x |
| 10 | 1MB | 1.4x | 3.0x | 2.2x |
| 100 | 1KB | 0.8x | 0.6x | 0.7x |
| 100 | 100KB | 1.0x | 2.0x | 0.9x |
| 100 | 1MB | 1.8x | 4.2x | 2.1x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 0.6x | 2.9x | 2.2x |
| 1 | 256B | 2.2x | 6.1x | 4.1x |
| 10 | 64B | 3.2x | 14.9x | 13.1x |
| 10 | 256B | 3.3x | 15.0x | 12.0x |
| 50 | 64B | 3.4x | 16.7x | 14.1x |
| 50 | 256B | 3.9x | 14.8x | 12.3x |
| 1000 | 64B | 1.2x | 13.9x | 16.5x |
| 1000 | 256B | 1.6x | 17.7x | 34.1x |

---

*Generated by NetConduit comparison benchmark suite*
