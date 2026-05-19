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
| 1 | 1KB | 8.3 | 6.8 | 7.1 | **1.23x** | **1.17x** |
| 1 | 100KB | 421.1 | 290.6 | 650.2 | **1.45x** | 0.65x |
| 1 | 1MB | 992.3 | 1,265.6 | 2,269.0 | 0.78x | 0.44x |
| 10 | 1KB | 28.7 | 34.9 | 26.1 | 0.82x | **1.10x** |
| 10 | 100KB | 1,331.2 | 765.5 | 1,473.3 | **1.74x** | 0.90x |
| 10 | 1MB | 1,742.1 | 1,660.0 | 2,967.1 | **1.05x** | 0.59x |
| 100 | 1KB | 88.3 | 46.0 | 46.4 | **1.92x** | **1.90x** |
| 100 | 100KB | 1,974.9 | 911.0 | 2,270.6 | **2.17x** | 0.87x |
| 100 | 1MB | 1,808.6 | 1,580.3 | 3,014.2 | **1.14x** | 0.60x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 930,051 | 351,194 | 293,188 | **2.65x** | **3.17x** |
| 1 | 256B | 799,239 | 193,536 | 285,792 | **4.13x** | **2.80x** |
| 10 | 64B | 1,033,646 | 211,180 | 275,746 | **4.89x** | **3.75x** |
| 10 | 256B | 782,273 | 220,450 | 253,201 | **3.55x** | **3.09x** |
| 50 | 64B | 1,203,728 | 239,860 | 256,436 | **5.02x** | **4.69x** |
| 50 | 256B | 846,811 | 232,512 | 250,835 | **3.64x** | **3.38x** |
| 1000 | 64B | 4,184,497 | 270,917 | 212,524 | **15.45x** | **19.69x** |
| 1000 | 256B | 2,524,669 | 483,099 | 219,942 | **5.23x** | **11.48x** |

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
| 1 | 1KB | 3.7 | 6.8 | 7.1 | 2.2 | 8.3 |
| 1 | 100KB | 326.4 | 290.6 | 650.2 | 272.3 | 421.1 |
| 1 | 1MB | 2,200.8 | 1,265.6 | 2,269.0 | 2,031.7 | 992.3 |
| 10 | 1KB | 18.1 | 34.9 | 26.1 | 8.1 | 28.7 |
| 10 | 100KB | 1,688.7 | 765.5 | 1,473.3 | 1,059.2 | 1,331.2 |
| 10 | 1MB | 6,615.6 | 1,660.0 | 2,967.1 | 2,986.1 | 1,742.1 |
| 100 | 1KB | 23.9 | 46.0 | 46.4 | 17.2 | 88.3 |
| 100 | 100KB | 2,019.8 | 911.0 | 2,270.6 | 1,885.3 | 1,974.9 |
| 100 | 1MB | 7,578.3 | 1,580.3 | 3,014.2 | 4,175.9 | 1,808.6 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 574,131 | 351,194 | 293,188 | 2,501,828 | 930,051 |
| 1 | 256B | 1,190,878 | 193,536 | 285,792 | 1,995,136 | 799,239 |
| 10 | 64B | 3,071,796 | 211,180 | 275,746 | — | 1,033,646 |
| 10 | 256B | 3,358,212 | 220,450 | 253,201 | 3,485,830 | 782,273 |
| 50 | 64B | 3,872,746 | 239,860 | 256,436 | — | 1,203,728 |
| 50 | 256B | 3,391,106 | 232,512 | 250,835 | — | 846,811 |
| 1000 | 64B | 3,652,634 | 270,917 | 212,524 | 4,942,147 | 4,184,497 |
| 1000 | 256B | 3,221,584 | 483,099 | 219,942 | 3,854,963 | 2,524,669 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.3x | 0.6x | 0.5x |
| 1 | 100KB | 0.6x | 1.1x | 0.5x |
| 1 | 1MB | 2.0x | 1.7x | 1.0x |
| 10 | 1KB | 0.3x | 0.5x | 0.7x |
| 10 | 100KB | 0.8x | 2.2x | 1.1x |
| 10 | 1MB | 1.7x | 4.0x | 2.2x |
| 100 | 1KB | 0.2x | 0.5x | 0.5x |
| 100 | 100KB | 1.0x | 2.2x | 0.9x |
| 100 | 1MB | 2.3x | 4.8x | 2.5x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.7x | 1.6x | 2.0x |
| 1 | 256B | 2.5x | 6.2x | 4.2x |
| 10 | 64B | 3.0x | 14.5x | 11.1x |
| 10 | 256B | 4.5x | 15.2x | 13.3x |
| 50 | 64B | 3.2x | 16.1x | 15.1x |
| 50 | 256B | 4.0x | 14.6x | 13.5x |
| 1000 | 64B | 1.2x | 13.5x | 17.2x |
| 1000 | 256B | 1.5x | 6.7x | 14.6x |

---

*Generated by NetConduit comparison benchmark suite*
