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
| 1 | 1KB | 10.8 | 8.2 | 7.6 | **1.33x** | **1.43x** |
| 1 | 100KB | 707.7 | 371.0 | 801.4 | **1.91x** | 0.88x |
| 1 | 1MB | 1,455.8 | 1,348.7 | 2,825.5 | **1.08x** | 0.52x |
| 10 | 1KB | 66.7 | 41.7 | 34.7 | **1.60x** | **1.92x** |
| 10 | 100KB | 1,169.8 | 817.9 | 1,485.1 | **1.43x** | 0.79x |
| 10 | 1MB | 1,862.0 | 1,970.3 | 3,194.1 | 0.94x | 0.58x |
| 100 | 1KB | 144.8 | 41.4 | 47.3 | **3.50x** | **3.06x** |
| 100 | 100KB | 2,505.5 | 1,100.2 | 2,069.4 | **2.28x** | **1.21x** |
| 100 | 1MB | 1,908.0 | 2,041.9 | 3,344.9 | 0.93x | 0.57x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,080,035 | 244,788 | 317,307 | **4.41x** | **3.40x** |
| 1 | 256B | 836,999 | 216,393 | 307,518 | **3.87x** | **2.72x** |
| 10 | 64B | 1,117,928 | 249,163 | 290,757 | **4.49x** | **3.84x** |
| 10 | 256B | 831,057 | 249,476 | 298,134 | **3.33x** | **2.79x** |
| 50 | 64B | 1,335,018 | 262,596 | 284,688 | **5.08x** | **4.69x** |
| 50 | 256B | 924,202 | 255,236 | 269,837 | **3.62x** | **3.43x** |
| 1000 | 64B | 4,278,537 | 342,396 | 242,615 | **12.50x** | **17.64x** |
| 1000 | 256B | 2,648,609 | 508,052 | 244,469 | **5.21x** | **10.83x** |

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
| 1 | 1KB | 4.3 | 8.2 | 7.6 | 3.0 | 10.8 |
| 1 | 100KB | 485.0 | 371.0 | 801.4 | 269.3 | 707.7 |
| 1 | 1MB | 2,484.3 | 1,348.7 | 2,825.5 | 2,022.7 | 1,455.8 |
| 10 | 1KB | 25.9 | 41.7 | 34.7 | 9.6 | 66.7 |
| 10 | 100KB | 1,980.7 | 817.9 | 1,485.1 | 945.9 | 1,169.8 |
| 10 | 1MB | 5,395.0 | 1,970.3 | 3,194.1 | 3,670.0 | 1,862.0 |
| 100 | 1KB | 25.7 | 41.4 | 47.3 | 27.9 | 144.8 |
| 100 | 100KB | 2,380.9 | 1,100.2 | 2,069.4 | 1,712.4 | 2,505.5 |
| 100 | 1MB | 9,035.4 | 2,041.9 | 3,344.9 | 4,649.9 | 1,908.0 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 1,111,388 | 244,788 | 317,307 | 2,695,857 | 1,080,035 |
| 1 | 256B | 1,200,436 | 216,393 | 307,518 | 2,396,216 | 836,999 |
| 10 | 64B | 3,803,430 | 249,163 | 290,757 | 4,587,189 | 1,117,928 |
| 10 | 256B | 3,530,679 | 249,476 | 298,134 | 2,307,576 | 831,057 |
| 50 | 64B | 4,055,282 | 262,596 | 284,688 | — | 1,335,018 |
| 50 | 256B | 3,604,756 | 255,236 | 269,837 | 4,152,287 | 924,202 |
| 1000 | 64B | 4,077,685 | 342,396 | 242,615 | 5,332,983 | 4,278,537 |
| 1000 | 256B | 3,642,156 | 508,052 | 244,469 | 4,246,755 | 2,648,609 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.3x | 0.5x | 0.6x |
| 1 | 100KB | 0.4x | 1.3x | 0.6x |
| 1 | 1MB | 1.4x | 1.8x | 0.9x |
| 10 | 1KB | 0.1x | 0.6x | 0.7x |
| 10 | 100KB | 0.8x | 2.4x | 1.3x |
| 10 | 1MB | 2.0x | 2.7x | 1.7x |
| 100 | 1KB | 0.2x | 0.6x | 0.5x |
| 100 | 100KB | 0.7x | 2.2x | 1.2x |
| 100 | 1MB | 2.4x | 4.4x | 2.7x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.5x | 4.5x | 3.5x |
| 1 | 256B | 2.9x | 5.5x | 3.9x |
| 10 | 64B | 4.1x | 15.3x | 13.1x |
| 10 | 256B | 2.8x | 14.2x | 11.8x |
| 50 | 64B | 3.0x | 15.4x | 14.2x |
| 50 | 256B | 4.5x | 14.1x | 13.4x |
| 1000 | 64B | 1.2x | 11.9x | 16.8x |
| 1000 | 256B | 1.6x | 7.2x | 14.9x |

---

*Generated by NetConduit comparison benchmark suite*
