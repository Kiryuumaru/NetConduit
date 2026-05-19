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
| 1 | 1KB | 6.2 | 8.4 | 6.8 | 0.75x | 0.91x |
| 1 | 100KB | 618.1 | 332.4 | 573.1 | **1.86x** | **1.08x** |
| 1 | 1MB | 884.3 | 1,246.9 | 2,230.9 | 0.71x | 0.40x |
| 10 | 1KB | 80.5 | 44.7 | 31.2 | **1.80x** | **2.58x** |
| 10 | 100KB | 1,228.2 | 859.3 | 1,819.5 | **1.43x** | 0.68x |
| 10 | 1MB | 2,127.8 | 1,762.1 | 3,256.3 | **1.21x** | 0.65x |
| 100 | 1KB | 78.8 | 43.7 | 36.8 | **1.80x** | **2.14x** |
| 100 | 100KB | 1,153.4 | 1,078.0 | 2,273.4 | **1.07x** | 0.51x |
| 100 | 1MB | 1,831.4 | 1,891.3 | 3,771.3 | 0.97x | 0.49x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,050,545 | 219,349 | 286,056 | **4.79x** | **3.67x** |
| 1 | 256B | 793,592 | 192,422 | 266,478 | **4.12x** | **2.98x** |
| 10 | 64B | 1,087,339 | 217,443 | 259,128 | **5.00x** | **4.20x** |
| 10 | 256B | 876,518 | 220,417 | 262,828 | **3.98x** | **3.33x** |
| 50 | 64B | 1,358,583 | 249,111 | 244,432 | **5.45x** | **5.56x** |
| 50 | 256B | 947,160 | 182,194 | 187,915 | **5.20x** | **5.04x** |
| 1000 | 64B | 4,216,993 | 155,442 | 139,218 | **27.13x** | **30.29x** |
| 1000 | 256B | 2,308,882 | 455,928 | 223,607 | **5.06x** | **10.33x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 9/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 4.6 | 8.4 | 6.8 | 2.5 | 6.2 |
| 1 | 100KB | 330.8 | 332.4 | 573.1 | 339.4 | 618.1 |
| 1 | 1MB | 2,209.5 | 1,246.9 | 2,230.9 | 1,886.4 | 884.3 |
| 10 | 1KB | 24.4 | 44.7 | 31.2 | 13.3 | 80.5 |
| 10 | 100KB | 1,673.9 | 859.3 | 1,819.5 | 613.2 | 1,228.2 |
| 10 | 1MB | 6,909.5 | 1,762.1 | 3,256.3 | 2,943.6 | 2,127.8 |
| 100 | 1KB | 24.0 | 43.7 | 36.8 | 22.2 | 78.8 |
| 100 | 100KB | 1,788.3 | 1,078.0 | 2,273.4 | 1,419.2 | 1,153.4 |
| 100 | 1MB | 7,826.1 | 1,891.3 | 3,771.3 | 4,352.1 | 1,831.4 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 685,859 | 219,349 | 286,056 | 2,561,357 | 1,050,545 |
| 1 | 256B | 1,080,440 | 192,422 | 266,478 | 2,349,411 | 793,592 |
| 10 | 64B | 3,412,154 | 217,443 | 259,128 | 2,622,211 | 1,087,339 |
| 10 | 256B | 687,759 | 220,417 | 262,828 | 4,176,747 | 876,518 |
| 50 | 64B | 3,752,785 | 249,111 | 244,432 | 4,807,722 | 1,358,583 |
| 50 | 256B | 1,872,369 | 182,194 | 187,915 | 4,018,538 | 947,160 |
| 1000 | 64B | 2,054,236 | 155,442 | 139,218 | 5,540,845 | 4,216,993 |
| 1000 | 256B | 2,089,480 | 455,928 | 223,607 | 4,127,054 | 2,308,882 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.4x | 0.5x | 0.7x |
| 1 | 100KB | 0.5x | 1.0x | 0.6x |
| 1 | 1MB | 2.1x | 1.8x | 1.0x |
| 10 | 1KB | 0.2x | 0.5x | 0.8x |
| 10 | 100KB | 0.5x | 1.9x | 0.9x |
| 10 | 1MB | 1.4x | 3.9x | 2.1x |
| 100 | 1KB | 0.3x | 0.5x | 0.7x |
| 100 | 100KB | 1.2x | 1.7x | 0.8x |
| 100 | 1MB | 2.4x | 4.1x | 2.1x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.4x | 3.1x | 2.4x |
| 1 | 256B | 3.0x | 5.6x | 4.1x |
| 10 | 64B | 2.4x | 15.7x | 13.2x |
| 10 | 256B | 4.8x | 3.1x | 2.6x |
| 50 | 64B | 3.5x | 15.1x | 15.4x |
| 50 | 256B | 4.2x | 10.3x | 10.0x |
| 1000 | 64B | 1.3x | 13.2x | 14.8x |
| 1000 | 256B | 1.8x | 4.6x | 9.3x |

---

*Generated by NetConduit comparison benchmark suite*
