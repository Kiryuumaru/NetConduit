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
| 1 | 1KB | 4.4 | 6.2 | 6.3 | 0.71x | 0.69x |
| 1 | 100KB | 461.1 | 210.9 | 412.8 | **2.19x** | **1.12x** |
| 1 | 1MB | 1,151.9 | 926.7 | 1,803.9 | **1.24x** | 0.64x |
| 10 | 1KB | 43.0 | 33.2 | 25.7 | **1.29x** | **1.68x** |
| 10 | 100KB | 1,245.0 | 825.7 | 1,541.8 | **1.51x** | 0.81x |
| 10 | 1MB | 1,528.6 | 1,637.3 | 3,104.6 | 0.93x | 0.49x |
| 100 | 1KB | 62.1 | 46.8 | 37.9 | **1.33x** | **1.64x** |
| 100 | 100KB | 1,482.0 | 1,009.2 | 1,991.3 | **1.47x** | 0.74x |
| 100 | 1MB | 1,998.3 | 1,821.6 | 3,348.7 | **1.10x** | 0.60x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 984,174 | 223,522 | 281,732 | **4.40x** | **3.49x** |
| 1 | 256B | 783,031 | 193,252 | 290,464 | **4.05x** | **2.70x** |
| 10 | 64B | 1,055,823 | 276,442 | 288,752 | **3.82x** | **3.66x** |
| 10 | 256B | 854,121 | 246,478 | 264,472 | **3.47x** | **3.23x** |
| 50 | 64B | 1,310,109 | 240,453 | 254,334 | **5.45x** | **5.15x** |
| 50 | 256B | 911,868 | 240,260 | 252,897 | **3.80x** | **3.61x** |
| 1000 | 64B | 4,082,784 | 318,058 | 209,817 | **12.84x** | **19.46x** |
| 1000 | 256B | 2,507,965 | 294,756 | 231,760 | **8.51x** | **10.82x** |

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
| 1 | 1KB | 2.4 | 6.2 | 6.3 | 2.4 | 4.4 |
| 1 | 100KB | 227.9 | 210.9 | 412.8 | 255.0 | 461.1 |
| 1 | 1MB | 1,343.5 | 926.7 | 1,803.9 | 2,086.4 | 1,151.9 |
| 10 | 1KB | 20.9 | 33.2 | 25.7 | 10.7 | 43.0 |
| 10 | 100KB | 1,613.0 | 825.7 | 1,541.8 | 1,100.0 | 1,245.0 |
| 10 | 1MB | 4,901.6 | 1,637.3 | 3,104.6 | 3,240.3 | 1,528.6 |
| 100 | 1KB | 24.2 | 46.8 | 37.9 | 21.2 | 62.1 |
| 100 | 100KB | 2,033.3 | 1,009.2 | 1,991.3 | 2,129.7 | 1,482.0 |
| 100 | 1MB | 8,228.7 | 1,821.6 | 3,348.7 | 4,118.8 | 1,998.3 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 596,878 | 223,522 | 281,732 | 2,543,322 | 984,174 |
| 1 | 256B | 1,050,129 | 193,252 | 290,464 | 2,360,662 | 783,031 |
| 10 | 64B | 3,431,634 | 276,442 | 288,752 | — | 1,055,823 |
| 10 | 256B | 3,511,624 | 246,478 | 264,472 | — | 854,121 |
| 50 | 64B | 3,858,182 | 240,453 | 254,334 | 4,693,548 | 1,310,109 |
| 50 | 256B | 3,517,276 | 240,260 | 252,897 | 2,306,468 | 911,868 |
| 1000 | 64B | 3,796,474 | 318,058 | 209,817 | 5,309,228 | 4,082,784 |
| 1000 | 256B | 3,814,564 | 294,756 | 231,760 | 3,796,885 | 2,507,965 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.6x | 0.4x | 0.4x |
| 1 | 100KB | 0.6x | 1.1x | 0.6x |
| 1 | 1MB | 1.8x | 1.4x | 0.7x |
| 10 | 1KB | 0.2x | 0.6x | 0.8x |
| 10 | 100KB | 0.9x | 2.0x | 1.0x |
| 10 | 1MB | 2.1x | 3.0x | 1.6x |
| 100 | 1KB | 0.3x | 0.5x | 0.6x |
| 100 | 100KB | 1.4x | 2.0x | 1.0x |
| 100 | 1MB | 2.1x | 4.5x | 2.5x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.6x | 2.7x | 2.1x |
| 1 | 256B | 3.0x | 5.4x | 3.6x |
| 10 | 64B | 3.3x | 12.4x | 11.9x |
| 10 | 256B | 4.1x | 14.2x | 13.3x |
| 50 | 64B | 3.6x | 16.0x | 15.2x |
| 50 | 256B | 2.5x | 14.6x | 13.9x |
| 1000 | 64B | 1.3x | 11.9x | 18.1x |
| 1000 | 256B | 1.5x | 12.9x | 16.5x |

---

*Generated by NetConduit comparison benchmark suite*
