# NetConduit Comparison Benchmark Results

All benchmarks run on loopback (127.0.0.1), identical workloads,
3 runs with median reported. CPU-pinned via `taskset 0x3` (2 cores).

**Fairness controls:** Both Go and .NET benchmarks pinned to same 2 CPU cores,
GOMAXPROCS=2, `--network none` when Docker, `taskset` when native.

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
| 1 | 1KB | 2.8 | 9.6 | 9.9 | 0.29x | 0.28x |
| 1 | 100KB | 235.9 | 494.3 | 569.7 | 0.48x | 0.41x |
| 1 | 1MB | 719.7 | 1,079.0 | 1,278.2 | 0.67x | 0.56x |
| 10 | 1KB | 22.1 | 18.6 | 18.2 | **1.19x** | **1.22x** |
| 10 | 100KB | 521.5 | 621.0 | 884.1 | 0.84x | 0.59x |
| 10 | 1MB | 909.8 | 1,239.0 | 1,740.3 | 0.73x | 0.52x |
| 100 | 1KB | 4.5 | 25.2 | 21.6 | 0.18x | 0.21x |
| 100 | 100KB | 502.1 | 617.2 | 996.6 | 0.81x | 0.50x |
| 100 | 1MB | 826.1 | 1,130.3 | 1,739.2 | 0.73x | 0.48x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,576,470 | 87,024 | 116,876 | **18.12x** | **13.49x** |
| 1 | 256B | 1,215,000 | 85,142 | 116,803 | **14.27x** | **10.40x** |
| 10 | 64B | 1,139,422 | 105,100 | 129,179 | **10.84x** | **8.82x** |
| 10 | 256B | 829,057 | 101,634 | 128,597 | **8.16x** | **6.45x** |
| 50 | 64B | 1,120,278 | 108,462 | 125,004 | **10.33x** | **8.96x** |
| 50 | 256B | 839,070 | 105,454 | 123,642 | **7.96x** | **6.79x** |
| 1000 | 64B | 1,109,727 | 109,760 | 107,002 | **10.11x** | **10.37x** |
| 1000 | 256B | 810,658 | 267,598 | 104,312 | **3.03x** | **7.77x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 2/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.
At 10 channels with 1KB payloads, NetConduit delivers 22.1 MB/s
(1.2x FRP/Yamux, 1.2x Smux).

**Game-tick messaging:** NetConduit wins 16/16 comparisons against Go multiplexers.
NetConduit reaches **3.0–18.1x faster than FRP/Yamux**
and **6.4–13.5x faster than Smux**.
At 1 channel with 64B messages, NetConduit delivers 1,576,470 msg/s.

**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,
priority queuing ensures critical channels aren't starved, and adaptive windowing
automatically reclaims memory from idle channels. These features add measurable
overhead in bulk transfer but provide production safety guarantees that simpler
muxes don't offer.

---

## Raw TCP Baselines

Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.
This is the theoretical ceiling, not a practical alternative (connection limits,
no flow control, no channel management).

### Throughput: All Implementations (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|-----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 1KB | 7.3 | 9.6 | 9.9 | 1.7 | 2.8 |
| 1 | 100KB | 532.9 | 494.3 | 569.7 | 380.6 | 235.9 |
| 1 | 1MB | 2,041.3 | 1,079.0 | 1,278.2 | 1,817.2 | 719.7 |
| 10 | 1KB | 13.7 | 18.6 | 18.2 | 7.7 | 22.1 |
| 10 | 100KB | 967.7 | 621.0 | 884.1 | 554.8 | 521.5 |
| 10 | 1MB | 2,920.6 | 1,239.0 | 1,740.3 | 2,379.2 | 909.8 |
| 100 | 1KB | 12.7 | 25.2 | 21.6 | 10.0 | 4.5 |
| 100 | 100KB | 992.8 | 617.2 | 996.6 | 765.6 | 502.1 |
| 100 | 1MB | 3,108.5 | 1,130.3 | 1,739.2 | 1,662.0 | 826.1 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 212,769 | 87,024 | 116,876 | 1,580,807 | 1,576,470 |
| 1 | 256B | 230,280 | 85,142 | 116,803 | 1,367,779 | 1,215,000 |
| 10 | 64B | 1,448,008 | 105,100 | 129,179 | 1,602,570 | 1,139,422 |
| 10 | 256B | 1,453,954 | 101,634 | 128,597 | — | 829,057 |
| 50 | 64B | 1,636,296 | 108,462 | 125,004 | — | 1,120,278 |
| 50 | 256B | 1,565,450 | 105,454 | 123,642 | 1,824,032 | 839,070 |
| 1000 | 64B | 1,677,525 | 109,760 | 107,002 | 2,518,202 | 1,109,727 |
| 1000 | 256B | 1,517,124 | 267,598 | 104,312 | 2,126,202 | 810,658 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.6x | 0.8x | 0.7x |
| 1 | 100KB | 1.6x | 1.1x | 0.9x |
| 1 | 1MB | 2.5x | 1.9x | 1.6x |
| 10 | 1KB | 0.4x | 0.7x | 0.8x |
| 10 | 100KB | 1.1x | 1.6x | 1.1x |
| 10 | 1MB | 2.6x | 2.4x | 1.7x |
| 100 | 1KB | 2.2x | 0.5x | 0.6x |
| 100 | 100KB | 1.5x | 1.6x | 1.0x |
| 100 | 1MB | 2.0x | 2.8x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.4x | 1.8x |
| 1 | 256B | 1.1x | 2.7x | 2.0x |
| 10 | 64B | 1.4x | 13.8x | 11.2x |
| 10 | 256B | — | 14.3x | 11.3x |
| 50 | 64B | — | 15.1x | 13.1x |
| 50 | 256B | 2.2x | 14.8x | 12.7x |
| 1000 | 64B | 2.3x | 15.3x | 15.7x |
| 1000 | 256B | 2.6x | 5.7x | 14.5x |

---

*Generated by NetConduit comparison benchmark suite — median of 3 runs*
