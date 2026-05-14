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
| 1 | 1KB | 5.3 | 13.2 | 10.7 | 0.40x | 0.49x |
| 1 | 100KB | 729.3 | 339.8 | 898.3 | **2.15x** | 0.81x |
| 1 | 1MB | 1,198.5 | 1,621.9 | 2,664.8 | 0.74x | 0.45x |
| 10 | 1KB | 100.0 | 49.1 | 42.5 | **2.04x** | **2.35x** |
| 10 | 100KB | 2,308.1 | 937.5 | 1,731.4 | **2.46x** | **1.33x** |
| 10 | 1MB | 2,854.3 | 2,980.1 | 4,081.4 | 0.96x | 0.70x |
| 100 | 1KB | 58.2 | 50.9 | 58.0 | **1.14x** | **1.00x** |
| 100 | 100KB | 2,287.4 | 1,288.4 | 2,268.5 | **1.78x** | **1.01x** |
| 100 | 1MB | 2,659.9 | 2,438.3 | 4,132.4 | **1.09x** | 0.64x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,140,551 | 246,015 | 326,407 | **4.64x** | **3.49x** |
| 1 | 256B | 1,147,881 | 212,599 | 336,030 | **5.40x** | **3.42x** |
| 10 | 64B | 1,242,974 | 237,982 | 287,982 | **5.22x** | **4.32x** |
| 10 | 256B | 1,176,395 | 246,564 | 286,267 | **4.77x** | **4.11x** |
| 50 | 64B | 1,546,152 | 273,151 | 293,880 | **5.66x** | **5.26x** |
| 50 | 256B | 1,232,333 | 262,566 | 280,286 | **4.69x** | **4.40x** |
| 1000 | 64B | 4,759,378 | 264,416 | 239,971 | **18.00x** | **19.83x** |
| 1000 | 256B | 2,686,055 | 370,360 | 246,764 | **7.25x** | **10.89x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 10/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 4.6 | 13.2 | 10.7 | 2.1 | 5.3 |
| 1 | 100KB | 486.1 | 339.8 | 898.3 | 291.6 | 729.3 |
| 1 | 1MB | 3,275.7 | 1,621.9 | 2,664.8 | 2,399.8 | 1,198.5 |
| 10 | 1KB | 25.8 | 49.1 | 42.5 | 9.0 | 100.0 |
| 10 | 100KB | 1,939.8 | 937.5 | 1,731.4 | 928.4 | 2,308.1 |
| 10 | 1MB | 8,498.2 | 2,980.1 | 4,081.4 | 2,994.8 | 2,854.3 |
| 100 | 1KB | 26.8 | 50.9 | 58.0 | 22.3 | 58.2 |
| 100 | 100KB | 2,284.1 | 1,288.4 | 2,268.5 | 2,363.4 | 2,287.4 |
| 100 | 1MB | 9,837.8 | 2,438.3 | 4,132.4 | 6,571.5 | 2,659.9 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 647,800 | 246,015 | 326,407 | 2,622,336 | 1,140,551 |
| 1 | 256B | 1,235,674 | 212,599 | 336,030 | 2,436,740 | 1,147,881 |
| 10 | 64B | 3,614,933 | 237,982 | 287,982 | — | 1,242,974 |
| 10 | 256B | 3,296,033 | 246,564 | 286,267 | — | 1,176,395 |
| 50 | 64B | 3,916,126 | 273,151 | 293,880 | 4,663,568 | 1,546,152 |
| 50 | 256B | 3,455,165 | 262,566 | 280,286 | 4,345,537 | 1,232,333 |
| 1000 | 64B | 3,813,299 | 264,416 | 239,971 | — | 4,759,378 |
| 1000 | 256B | 7,651,200 | 370,360 | 246,764 | 4,255,481 | 2,686,055 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.4x | 0.3x | 0.4x |
| 1 | 100KB | 0.4x | 1.4x | 0.5x |
| 1 | 1MB | 2.0x | 2.0x | 1.2x |
| 10 | 1KB | 0.1x | 0.5x | 0.6x |
| 10 | 100KB | 0.4x | 2.1x | 1.1x |
| 10 | 1MB | 1.0x | 2.9x | 2.1x |
| 100 | 1KB | 0.4x | 0.5x | 0.5x |
| 100 | 100KB | 1.0x | 1.8x | 1.0x |
| 100 | 1MB | 2.5x | 4.0x | 2.4x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.3x | 2.6x | 2.0x |
| 1 | 256B | 2.1x | 5.8x | 3.7x |
| 10 | 64B | 2.9x | 15.2x | 12.6x |
| 10 | 256B | 2.8x | 13.4x | 11.5x |
| 50 | 64B | 3.0x | 14.3x | 13.3x |
| 50 | 256B | 3.5x | 13.2x | 12.3x |
| 1000 | 64B | 0.8x | 14.4x | 15.9x |
| 1000 | 256B | 1.6x | 20.7x | 31.0x |

---

*Generated by NetConduit comparison benchmark suite*
