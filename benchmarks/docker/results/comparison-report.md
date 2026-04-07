# NetConduit Comparison Benchmark Results

All benchmarks run on loopback (127.0.0.1), identical workloads,
5 runs with median reported. CPU-pinned via `taskset 0x3` (2 cores).

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
| 1 | 1KB | 5.9 | 2.0 | 6.1 | **2.98x** | 0.97x |
| 1 | 100KB | 281.0 | 306.0 | 377.6 | 0.92x | 0.74x |
| 1 | 1MB | 569.3 | 671.8 | 926.6 | 0.85x | 0.61x |
| 10 | 1KB | 22.3 | 14.4 | 13.7 | **1.55x** | **1.62x** |
| 10 | 100KB | 699.6 | 450.2 | 562.2 | **1.55x** | **1.24x** |
| 10 | 1MB | 752.7 | 866.9 | 1,089.5 | 0.87x | 0.69x |
| 100 | 1KB | 17.7 | 18.3 | 13.9 | 0.97x | **1.27x** |
| 100 | 100KB | 430.3 | 654.7 | 979.8 | 0.66x | 0.44x |
| 100 | 1MB | 490.4 | 1,196.8 | 1,646.4 | 0.41x | 0.30x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 109,248 | 90,106 | 118,082 | **1.21x** | 0.93x |
| 1 | 256B | 161,876 | 90,492 | 119,611 | **1.79x** | **1.35x** |
| 10 | 64B | 146,025 | 105,488 | 134,086 | **1.38x** | **1.09x** |
| 10 | 256B | 105,910 | 107,986 | 133,707 | 0.98x | 0.79x |
| 50 | 64B | 130,327 | 113,838 | 131,083 | **1.14x** | 0.99x |
| 50 | 256B | 112,230 | 111,042 | 125,492 | **1.01x** | 0.89x |
| 1000 | 64B | 105,784 | 111,339 | 108,636 | 0.95x | 0.97x |
| 1000 | 256B | 99,979 | 290,950 | 109,222 | 0.34x | 0.92x |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 6/18 comparisons against Go multiplexers.
Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.
NetConduit is competitive or faster for small-to-medium payloads (1KB–100KB).

**Game-tick messaging:** NetConduit wins 7/16 comparisons against Go multiplexers.
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
| 1 | 1KB | 1.7 | 2.0 | 6.1 | 2.7 | 5.9 |
| 1 | 100KB | 360.1 | 306.0 | 377.6 | 322.1 | 281.0 |
| 1 | 1MB | 1,404.8 | 671.8 | 926.6 | 1,860.1 | 569.3 |
| 10 | 1KB | 10.0 | 14.4 | 13.7 | 10.0 | 22.3 |
| 10 | 100KB | 765.7 | 450.2 | 562.2 | 874.2 | 699.6 |
| 10 | 1MB | 2,108.8 | 866.9 | 1,089.5 | 2,718.9 | 752.7 |
| 100 | 1KB | 7.5 | 18.3 | 13.9 | 10.7 | 17.7 |
| 100 | 100KB | 605.8 | 654.7 | 979.8 | 530.8 | 430.3 |
| 100 | 1MB | 3,056.9 | 1,196.8 | 1,646.4 | 1,524.0 | 490.4 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 241,354 | 90,106 | 118,082 | 1,559,903 | 109,248 |
| 1 | 256B | 237,794 | 90,492 | 119,611 | 1,412,503 | 161,876 |
| 10 | 64B | 1,529,214 | 105,488 | 134,086 | — | 146,025 |
| 10 | 256B | 1,534,198 | 107,986 | 133,707 | — | 105,910 |
| 50 | 64B | 1,697,910 | 113,838 | 131,083 | 2,192,804 | 130,327 |
| 50 | 256B | 1,594,030 | 111,042 | 125,492 | — | 112,230 |
| 1000 | 64B | 2,475,396 | 111,339 | 108,636 | — | 105,784 |
| 1000 | 256B | 1,549,308 | 290,950 | 109,222 | 2,173,835 | 99,979 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.5x | 0.8x | 0.3x |
| 1 | 100KB | 1.1x | 1.2x | 1.0x |
| 1 | 1MB | 3.3x | 2.1x | 1.5x |
| 10 | 1KB | 0.5x | 0.7x | 0.7x |
| 10 | 100KB | 1.2x | 1.7x | 1.4x |
| 10 | 1MB | 3.6x | 2.4x | 1.9x |
| 100 | 1KB | 0.6x | 0.4x | 0.5x |
| 100 | 100KB | 1.2x | 0.9x | 0.6x |
| 100 | 1MB | 3.1x | 2.6x | 1.9x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 14.3x | 2.7x | 2.0x |
| 1 | 256B | 8.7x | 2.6x | 2.0x |
| 10 | 64B | 10.5x | 14.5x | 11.4x |
| 10 | 256B | 14.5x | 14.2x | 11.5x |
| 50 | 64B | 16.8x | 14.9x | 13.0x |
| 50 | 256B | 14.2x | 14.4x | 12.7x |
| 1000 | 64B | 23.4x | 22.2x | 22.8x |
| 1000 | 256B | 21.7x | 5.3x | 14.2x |

---

*Generated by NetConduit comparison benchmark suite*
