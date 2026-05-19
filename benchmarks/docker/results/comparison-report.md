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
| 1 | 1KB | 12.0 | 9.3 | 9.3 | **1.29x** | **1.29x** |
| 1 | 100KB | 618.5 | 337.1 | 780.7 | **1.83x** | 0.79x |
| 1 | 1MB | 1,190.9 | 1,367.9 | 2,996.9 | 0.87x | 0.40x |
| 10 | 1KB | 30.6 | 40.6 | 42.1 | 0.75x | 0.73x |
| 10 | 100KB | 1,443.8 | 897.7 | 1,931.8 | **1.61x** | 0.75x |
| 10 | 1MB | 2,112.2 | 2,051.9 | 4,350.3 | **1.03x** | 0.49x |
| 100 | 1KB | 84.1 | 52.3 | 42.7 | **1.61x** | **1.97x** |
| 100 | 100KB | 2,156.0 | 1,086.2 | 2,315.3 | **1.98x** | 0.93x |
| 100 | 1MB | 1,647.1 | 2,421.2 | 3,823.4 | 0.68x | 0.43x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 987,139 | 238,304 | 293,034 | **4.14x** | **3.37x** |
| 1 | 256B | 821,175 | 216,285 | 290,415 | **3.80x** | **2.83x** |
| 10 | 64B | 844,347 | 245,434 | 278,414 | **3.44x** | **3.03x** |
| 10 | 256B | 678,706 | 234,312 | 268,734 | **2.90x** | **2.53x** |
| 50 | 64B | 1,045,889 | 247,396 | 257,750 | **4.23x** | **4.06x** |
| 50 | 256B | 846,521 | 229,358 | 267,778 | **3.69x** | **3.16x** |
| 1000 | 64B | 4,258,558 | 283,587 | 216,788 | **15.02x** | **19.64x** |
| 1000 | 256B | 2,620,660 | 411,346 | 233,666 | **6.37x** | **11.22x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 8/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 4.5 | 9.3 | 9.3 | 4.0 | 12.0 |
| 1 | 100KB | 381.6 | 337.1 | 780.7 | 352.2 | 618.5 |
| 1 | 1MB | 2,969.3 | 1,367.9 | 2,996.9 | 2,363.5 | 1,190.9 |
| 10 | 1KB | 28.3 | 40.6 | 42.1 | 11.0 | 30.6 |
| 10 | 100KB | 2,256.3 | 897.7 | 1,931.8 | 921.6 | 1,443.8 |
| 10 | 1MB | 7,989.7 | 2,051.9 | 4,350.3 | 3,099.7 | 2,112.2 |
| 100 | 1KB | 27.9 | 52.3 | 42.7 | 17.6 | 84.1 |
| 100 | 100KB | 2,421.3 | 1,086.2 | 2,315.3 | 1,753.1 | 2,156.0 |
| 100 | 1MB | 9,571.0 | 2,421.2 | 3,823.4 | 4,552.2 | 1,647.1 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 674,812 | 238,304 | 293,034 | 2,593,800 | 987,139 |
| 1 | 256B | 1,167,026 | 216,285 | 290,415 | 2,379,328 | 821,175 |
| 10 | 64B | 3,723,492 | 245,434 | 278,414 | 2,740,900 | 844,347 |
| 10 | 256B | 3,187,452 | 234,312 | 268,734 | — | 678,706 |
| 50 | 64B | 3,920,276 | 247,396 | 257,750 | — | 1,045,889 |
| 50 | 256B | 3,524,304 | 229,358 | 267,778 | — | 846,521 |
| 1000 | 64B | 3,909,190 | 283,587 | 216,788 | — | 4,258,558 |
| 1000 | 256B | 3,471,274 | 411,346 | 233,666 | 4,250,770 | 2,620,660 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.3x | 0.5x | 0.5x |
| 1 | 100KB | 0.6x | 1.1x | 0.5x |
| 1 | 1MB | 2.0x | 2.2x | 1.0x |
| 10 | 1KB | 0.4x | 0.7x | 0.7x |
| 10 | 100KB | 0.6x | 2.5x | 1.2x |
| 10 | 1MB | 1.5x | 3.9x | 1.8x |
| 100 | 1KB | 0.2x | 0.5x | 0.7x |
| 100 | 100KB | 0.8x | 2.2x | 1.0x |
| 100 | 1MB | 2.8x | 4.0x | 2.5x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.6x | 2.8x | 2.3x |
| 1 | 256B | 2.9x | 5.4x | 4.0x |
| 10 | 64B | 3.2x | 15.2x | 13.4x |
| 10 | 256B | 4.7x | 13.6x | 11.9x |
| 50 | 64B | 3.7x | 15.8x | 15.2x |
| 50 | 256B | 4.2x | 15.4x | 13.2x |
| 1000 | 64B | 0.9x | 13.8x | 18.0x |
| 1000 | 256B | 1.6x | 8.4x | 14.9x |

---

*Generated by NetConduit comparison benchmark suite*
