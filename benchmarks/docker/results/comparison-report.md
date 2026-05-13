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
| 1 | 1KB | 13.1 | 15.6 | 8.8 | 0.84x | **1.48x** |
| 1 | 100KB | 764.7 | 405.9 | 698.8 | **1.88x** | **1.09x** |
| 1 | 1MB | 1,232.6 | 1,500.5 | 3,418.3 | 0.82x | 0.36x |
| 10 | 1KB | 71.2 | 39.4 | 34.8 | **1.81x** | **2.05x** |
| 10 | 100KB | 1,542.3 | 1,113.8 | 1,366.6 | **1.38x** | **1.13x** |
| 10 | 1MB | 3,091.1 | 2,427.2 | 4,148.3 | **1.27x** | 0.75x |
| 100 | 1KB | 80.0 | 76.3 | 51.2 | **1.05x** | **1.56x** |
| 100 | 100KB | 2,578.5 | 1,404.6 | 2,698.8 | **1.84x** | 0.96x |
| 100 | 1MB | 2,525.3 | 2,255.9 | 3,988.7 | **1.12x** | 0.63x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,294,623 | 289,437 | 382,181 | **4.47x** | **3.39x** |
| 1 | 256B | 1,297,584 | 262,338 | 382,632 | **4.95x** | **3.39x** |
| 10 | 64B | 1,441,000 | 305,204 | 353,102 | **4.72x** | **4.08x** |
| 10 | 256B | 1,341,964 | 299,320 | 344,618 | **4.48x** | **3.89x** |
| 50 | 64B | 1,648,098 | 292,727 | 299,324 | **5.63x** | **5.51x** |
| 50 | 256B | 1,354,002 | 275,987 | 310,138 | **4.91x** | **4.37x** |
| 1000 | 64B | 4,672,471 | 317,559 | 269,193 | **14.71x** | **17.36x** |
| 1000 | 256B | 2,784,946 | 545,141 | 257,266 | **5.11x** | **10.83x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 12/18 comparisons against Go multiplexers.
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
| 1 | 1KB | 6.0 | 15.6 | 8.8 | 1.3 | 13.1 |
| 1 | 100KB | 458.8 | 405.9 | 698.8 | 359.7 | 764.7 |
| 1 | 1MB | 3,514.4 | 1,500.5 | 3,418.3 | 3,386.4 | 1,232.6 |
| 10 | 1KB | 25.2 | 39.4 | 34.8 | 9.8 | 71.2 |
| 10 | 100KB | 1,945.0 | 1,113.8 | 1,366.6 | 1,019.4 | 1,542.3 |
| 10 | 1MB | 7,647.5 | 2,427.2 | 4,148.3 | 2,777.5 | 3,091.1 |
| 100 | 1KB | 25.3 | 76.3 | 51.2 | 32.4 | 80.0 |
| 100 | 100KB | 2,145.1 | 1,404.6 | 2,698.8 | 2,022.2 | 2,578.5 |
| 100 | 1MB | 9,327.5 | 2,255.9 | 3,988.7 | 4,710.3 | 2,525.3 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 1,133,093 | 289,437 | 382,181 | 2,905,828 | 1,294,623 |
| 1 | 256B | 1,376,078 | 262,338 | 382,632 | 2,708,717 | 1,297,584 |
| 10 | 64B | 4,420,616 | 305,204 | 353,102 | 4,632,563 | 1,441,000 |
| 10 | 256B | 3,973,660 | 299,320 | 344,618 | — | 1,341,964 |
| 50 | 64B | 4,434,825 | 292,727 | 299,324 | 5,039,191 | 1,648,098 |
| 50 | 256B | 3,631,054 | 275,987 | 310,138 | — | 1,354,002 |
| 1000 | 64B | 4,206,610 | 317,559 | 269,193 | 5,590,924 | 4,672,471 |
| 1000 | 256B | 3,778,697 | 545,141 | 257,266 | — | 2,784,946 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.1x | 0.4x | 0.7x |
| 1 | 100KB | 0.5x | 1.1x | 0.7x |
| 1 | 1MB | 2.7x | 2.3x | 1.0x |
| 10 | 1KB | 0.1x | 0.6x | 0.7x |
| 10 | 100KB | 0.7x | 1.7x | 1.4x |
| 10 | 1MB | 0.9x | 3.2x | 1.8x |
| 100 | 1KB | 0.4x | 0.3x | 0.5x |
| 100 | 100KB | 0.8x | 1.5x | 0.8x |
| 100 | 1MB | 1.9x | 4.1x | 2.3x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 2.2x | 3.9x | 3.0x |
| 1 | 256B | 2.1x | 5.2x | 3.6x |
| 10 | 64B | 3.2x | 14.5x | 12.5x |
| 10 | 256B | 3.0x | 13.3x | 11.5x |
| 50 | 64B | 3.1x | 15.2x | 14.8x |
| 50 | 256B | 2.7x | 13.2x | 11.7x |
| 1000 | 64B | 1.2x | 13.2x | 15.6x |
| 1000 | 256B | 1.4x | 6.9x | 14.7x |

---

*Generated by NetConduit comparison benchmark suite*
