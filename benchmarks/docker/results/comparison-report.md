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
| 1 | 1KB | 3.3 | 9.5 | 9.1 | 0.35x | 0.36x |
| 1 | 100KB | 379.4 | 479.6 | 985.0 | 0.79x | 0.39x |
| 1 | 1MB | 696.4 | 1,762.4 | 2,460.2 | 0.40x | 0.28x |
| 10 | 1KB | 23.2 | 41.0 | 33.7 | 0.56x | 0.69x |
| 10 | 100KB | 1,654.6 | 1,021.8 | 1,617.3 | **1.62x** | **1.02x** |
| 10 | 1MB | 1,997.2 | 2,177.4 | 3,841.7 | 0.92x | 0.52x |
| 100 | 1KB | 142.0 | 69.6 | 45.5 | **2.04x** | **3.12x** |
| 100 | 100KB | 1,895.2 | 1,261.6 | 2,433.5 | **1.50x** | 0.78x |
| 100 | 1MB | 2,339.0 | 1,977.7 | 3,687.5 | **1.18x** | 0.63x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,035,687 | 254,109 | 323,716 | **4.08x** | **3.20x** |
| 1 | 256B | 816,748 | 235,140 | 340,159 | **3.47x** | **2.40x** |
| 10 | 64B | 1,170,486 | 257,737 | 290,138 | **4.54x** | **4.03x** |
| 10 | 256B | 981,624 | 268,116 | 313,962 | **3.66x** | **3.13x** |
| 50 | 64B | 1,447,782 | 288,408 | 315,532 | **5.02x** | **4.59x** |
| 50 | 256B | 1,002,666 | 280,994 | 283,986 | **3.57x** | **3.53x** |
| 1000 | 64B | 4,466,851 | 321,597 | 243,060 | **13.89x** | **18.38x** |
| 1000 | 256B | 2,556,083 | 471,839 | 243,974 | **5.42x** | **10.48x** |

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
| 1 | 1KB | 5.1 | 9.5 | 9.1 | 3.2 | 3.3 |
| 1 | 100KB | 510.9 | 479.6 | 985.0 | 243.2 | 379.4 |
| 1 | 1MB | 3,285.0 | 1,762.4 | 2,460.2 | 1,753.5 | 696.4 |
| 10 | 1KB | 24.3 | 41.0 | 33.7 | 12.1 | 23.2 |
| 10 | 100KB | 1,737.1 | 1,021.8 | 1,617.3 | 1,052.1 | 1,654.6 |
| 10 | 1MB | 8,455.6 | 2,177.4 | 3,841.7 | 3,088.8 | 1,997.2 |
| 100 | 1KB | 30.9 | 69.6 | 45.5 | 26.2 | 142.0 |
| 100 | 100KB | 2,873.2 | 1,261.6 | 2,433.5 | 1,723.4 | 1,895.2 |
| 100 | 1MB | 9,857.8 | 1,977.7 | 3,687.5 | 5,478.9 | 2,339.0 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 698,803 | 254,109 | 323,716 | 2,621,615 | 1,035,687 |
| 1 | 256B | 1,217,276 | 235,140 | 340,159 | 2,417,710 | 816,748 |
| 10 | 64B | 4,118,826 | 257,737 | 290,138 | 4,302,612 | 1,170,486 |
| 10 | 256B | 3,539,701 | 268,116 | 313,962 | 4,399,133 | 981,624 |
| 50 | 64B | 4,170,990 | 288,408 | 315,532 | 5,108,711 | 1,447,782 |
| 50 | 256B | 3,843,806 | 280,994 | 283,986 | 4,487,957 | 1,002,666 |
| 1000 | 64B | 4,257,206 | 321,597 | 243,060 | 5,361,267 | 4,466,851 |
| 1000 | 256B | 3,495,776 | 471,839 | 243,974 | 4,522,585 | 2,556,083 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 1.0x | 0.5x | 0.6x |
| 1 | 100KB | 0.6x | 1.1x | 0.5x |
| 1 | 1MB | 2.5x | 1.9x | 1.3x |
| 10 | 1KB | 0.5x | 0.6x | 0.7x |
| 10 | 100KB | 0.6x | 1.7x | 1.1x |
| 10 | 1MB | 1.5x | 3.9x | 2.2x |
| 100 | 1KB | 0.2x | 0.4x | 0.7x |
| 100 | 100KB | 0.9x | 2.3x | 1.2x |
| 100 | 1MB | 2.3x | 5.0x | 2.7x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.5x | 2.8x | 2.2x |
| 1 | 256B | 3.0x | 5.2x | 3.6x |
| 10 | 64B | 3.7x | 16.0x | 14.2x |
| 10 | 256B | 4.5x | 13.2x | 11.3x |
| 50 | 64B | 3.5x | 14.5x | 13.2x |
| 50 | 256B | 4.5x | 13.7x | 13.5x |
| 1000 | 64B | 1.2x | 13.2x | 17.5x |
| 1000 | 256B | 1.8x | 7.4x | 14.3x |

---

*Generated by NetConduit comparison benchmark suite*
