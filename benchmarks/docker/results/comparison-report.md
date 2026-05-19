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
| 1 | 1KB | 10.1 | 9.3 | 10.0 | **1.09x** | **1.01x** |
| 1 | 100KB | 836.8 | 381.8 | 906.0 | **2.19x** | 0.92x |
| 1 | 1MB | 1,201.9 | 1,555.6 | 3,354.4 | 0.77x | 0.36x |
| 10 | 1KB | 54.6 | 37.7 | 37.4 | **1.45x** | **1.46x** |
| 10 | 100KB | 1,562.5 | 1,126.8 | 1,818.8 | **1.39x** | 0.86x |
| 10 | 1MB | 2,684.1 | 2,169.5 | 3,142.1 | **1.24x** | 0.85x |
| 100 | 1KB | 89.3 | 35.5 | 39.9 | **2.52x** | **2.24x** |
| 100 | 100KB | 1,641.6 | 1,154.6 | 2,388.9 | **1.42x** | 0.69x |
| 100 | 1MB | 2,064.2 | 2,160.8 | 3,748.4 | 0.96x | 0.55x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,077,404 | 223,686 | 290,714 | **4.82x** | **3.71x** |
| 1 | 256B | 899,690 | 209,745 | 312,957 | **4.29x** | **2.87x** |
| 10 | 64B | 1,181,620 | 249,077 | 289,805 | **4.74x** | **4.08x** |
| 10 | 256B | 900,995 | 240,971 | 270,887 | **3.74x** | **3.33x** |
| 50 | 64B | 1,433,225 | 239,114 | 278,616 | **5.99x** | **5.14x** |
| 50 | 256B | 997,521 | 259,510 | 295,642 | **3.84x** | **3.37x** |
| 1000 | 64B | 4,113,140 | 300,088 | 254,160 | **13.71x** | **16.18x** |
| 1000 | 256B | 2,522,902 | 484,079 | 245,792 | **5.21x** | **10.26x** |

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
| 1 | 1KB | 4.1 | 9.3 | 10.0 | 2.3 | 10.1 |
| 1 | 100KB | 417.6 | 381.8 | 906.0 | 296.1 | 836.8 |
| 1 | 1MB | 3,024.5 | 1,555.6 | 3,354.4 | 3,023.0 | 1,201.9 |
| 10 | 1KB | 22.5 | 37.7 | 37.4 | 8.2 | 54.6 |
| 10 | 100KB | 2,056.3 | 1,126.8 | 1,818.8 | 960.2 | 1,562.5 |
| 10 | 1MB | 7,465.2 | 2,169.5 | 3,142.1 | 2,963.5 | 2,684.1 |
| 100 | 1KB | 25.0 | 35.5 | 39.9 | 26.5 | 89.3 |
| 100 | 100KB | 2,650.6 | 1,154.6 | 2,388.9 | 1,975.3 | 1,641.6 |
| 100 | 1MB | 8,756.6 | 2,160.8 | 3,748.4 | 4,719.5 | 2,064.2 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 671,217 | 223,686 | 290,714 | 2,710,365 | 1,077,404 |
| 1 | 256B | 1,162,392 | 209,745 | 312,957 | 2,491,920 | 899,690 |
| 10 | 64B | 3,799,584 | 249,077 | 289,805 | — | 1,181,620 |
| 10 | 256B | 3,467,796 | 240,971 | 270,887 | — | 900,995 |
| 50 | 64B | 3,792,120 | 239,114 | 278,616 | — | 1,433,225 |
| 50 | 256B | 3,643,688 | 259,510 | 295,642 | 2,560,303 | 997,521 |
| 1000 | 64B | 4,013,993 | 300,088 | 254,160 | 4,847,030 | 4,113,140 |
| 1000 | 256B | 3,505,552 | 484,079 | 245,792 | 3,378,084 | 2,522,902 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 0.4x | 0.4x |
| 1 | 100KB | 0.4x | 1.1x | 0.5x |
| 1 | 1MB | 2.5x | 1.9x | 0.9x |
| 10 | 1KB | 0.1x | 0.6x | 0.6x |
| 10 | 100KB | 0.6x | 1.8x | 1.1x |
| 10 | 1MB | 1.1x | 3.4x | 2.4x |
| 100 | 1KB | 0.3x | 0.7x | 0.6x |
| 100 | 100KB | 1.2x | 2.3x | 1.1x |
| 100 | 1MB | 2.3x | 4.1x | 2.3x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.5x | 3.0x | 2.3x |
| 1 | 256B | 2.8x | 5.5x | 3.7x |
| 10 | 64B | 3.2x | 15.3x | 13.1x |
| 10 | 256B | 3.8x | 14.4x | 12.8x |
| 50 | 64B | 2.6x | 15.9x | 13.6x |
| 50 | 256B | 2.6x | 14.0x | 12.3x |
| 1000 | 64B | 1.2x | 13.4x | 15.8x |
| 1000 | 256B | 1.3x | 7.2x | 14.3x |

---

*Generated by NetConduit comparison benchmark suite*
