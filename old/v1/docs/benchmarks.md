# NetConduit Comparison Benchmark Results

All benchmarks run on loopback (127.0.0.1), identical workloads,
3 independent runs with median reported. Each run uses 5 iterations per
data point (median of medians). CPU-pinned via `taskset 0x3` (2 cores).

**Fairness controls:** Both Go and .NET benchmarks pinned to same 2 CPU cores,
GOMAXPROCS=2, `taskset` when native.

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
| 1 | 1KB | 7.0 | 7.8 | 8.3 | 0.90x | 0.84x |
| 1 | 100KB | 234.4 | 358.9 | 589.8 | 0.65x | 0.40x |
| 1 | 1MB | 714.8 | 1,048.7 | 1,265.2 | 0.68x | 0.56x |
| 10 | 1KB | 25.4 | 23.0 | 20.4 | **1.10x** | **1.25x** |
| 10 | 100KB | 494.5 | 471.1 | 818.3 | **1.05x** | 0.60x |
| 10 | 1MB | 771.7 | 1,062.9 | 1,743.4 | 0.73x | 0.44x |
| 100 | 1KB | 3.2 | 23.5 | 21.7 | 0.14x | 0.15x |
| 100 | 100KB | 342.8 | 658.6 | 967.7 | 0.52x | 0.35x |
| 100 | 1MB | 1,053.7 | 1,153.3 | 1,673.2 | 0.91x | 0.63x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,473,546 | 85,962 | 113,792 | **17.14x** | **12.95x** |
| 1 | 256B | 1,164,419 | 83,266 | 114,639 | **13.98x** | **10.16x** |
| 10 | 64B | 1,085,684 | 101,066 | 127,860 | **10.74x** | **8.49x** |
| 10 | 256B | 754,602 | 101,896 | 127,800 | **7.41x** | **5.90x** |
| 50 | 64B | 1,070,683 | 103,194 | 122,092 | **10.38x** | **8.77x** |
| 50 | 256B | 764,883 | 101,546 | 121,050 | **7.53x** | **6.32x** |
| 1000 | 64B | 1,122,550 | 103,388 | 99,812 | **10.86x** | **11.25x** |
| 1000 | 256B | 822,527 | 111,633 | 97,574 | **7.37x** | **8.43x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 3/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk
scenarios. NetConduit is fastest at 10 channels with small payloads (1KB), where it
delivers 25.4 MB/s (1.1x FRP/Yamux, 1.2x Smux). Single-channel large transfers show
a significant gap (0.40–0.90x) due to credit round-trip latency. At 100 channels,
contention in the credit system reduces throughput significantly.

**Game-tick messaging:** NetConduit wins 16/16 comparisons against Go multiplexers,
reaching **7.4–17.1x faster than FRP/Yamux** and **5.9–12.9x faster than Smux**.
At 1 channel with 64B messages, NetConduit delivers 1,473,546 msg/s. Performance
stays above 750K msg/s even at 1000 channels with 256B messages.

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

> **Note:** Raw TCP (.NET) at higher channel counts (10+) intermittently fails with
> `IOException` due to port exhaustion under rapid connection cycling. These failures
> are non-deterministic — the same scenario may succeed or fail between runs.
> NetConduit Mux TCP never exhibits this issue since it uses a single connection.

### Throughput: All Implementations (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|-----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 1KB | 6.5 | 7.8 | 8.3 | 1.3 | 7.0 |
| 1 | 100KB | 527.8 | 358.9 | 589.8 | 322.8 | 234.4 |
| 1 | 1MB | 2,070.8 | 1,048.7 | 1,265.2 | 1,393.7 | 714.8 |
| 10 | 1KB | 13.1 | 23.0 | 20.4 | 8.4 | 25.4 |
| 10 | 100KB | 1,076.1 | 471.1 | 818.3 | 739.2 | 494.5 |
| 10 | 1MB | 2,602.6 | 1,062.9 | 1,743.4 | 2,448.5 | 771.7 |
| 100 | 1KB | 12.4 | 23.5 | 21.7 | 7.3 | 3.2 |
| 100 | 100KB | 921.9 | 658.6 | 967.7 | 467.9 | 342.8 |
| 100 | 1MB | 2,969.8 | 1,153.3 | 1,673.2 | 2,082.2 | 1,053.7 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 212,424 | 85,962 | 113,792 | 1,463,723 | 1,473,546 |
| 1 | 256B | 224,695 | 83,266 | 114,639 | 1,320,995 | 1,164,419 |
| 10 | 64B | 1,245,233 | 101,066 | 127,860 | — | 1,085,684 |
| 10 | 256B | 1,439,120 | 101,896 | 127,800 | — | 754,602 |
| 50 | 64B | 1,596,122 | 103,194 | 122,092 | 1,641,251 | 1,070,683 |
| 50 | 256B | 1,526,810 | 101,546 | 121,050 | 1,832,242 | 764,883 |
| 1000 | 64B | 1,876,542 | 103,388 | 99,812 | — | 1,122,550 |
| 1000 | 256B | 1,491,365 | 111,633 | 97,574 | — | 822,527 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 0.8x | 0.8x |
| 1 | 100KB | 1.4x | 1.5x | 0.9x |
| 1 | 1MB | 1.9x | 2.0x | 1.6x |
| 10 | 1KB | 0.3x | 0.6x | 0.6x |
| 10 | 100KB | 1.5x | 2.3x | 1.3x |
| 10 | 1MB | 3.2x | 2.4x | 1.5x |
| 100 | 1KB | 2.3x | 0.5x | 0.6x |
| 100 | 100KB | 1.4x | 1.4x | 1.0x |
| 100 | 1MB | 2.0x | 2.6x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.5x | 1.9x |
| 1 | 256B | 1.1x | 2.7x | 2.0x |
| 10 | 64B | — | 12.3x | 9.7x |
| 10 | 256B | — | 14.1x | 11.3x |
| 50 | 64B | 1.5x | 15.5x | 13.1x |
| 50 | 256B | 2.4x | 15.0x | 12.6x |
| 1000 | 64B | — | 18.2x | 18.8x |
| 1000 | 256B | — | 13.4x | 15.3x |

---

*Generated by NetConduit comparison benchmark suite — median of 3 independent runs,
each run uses 5 iterations per data point with median selection*
