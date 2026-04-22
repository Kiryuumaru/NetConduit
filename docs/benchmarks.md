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
| 1 | 1KB | 8.1 | 8.4 | 9.9 | 0.96x | 0.82x |
| 1 | 100KB | 132.6 | 516.8 | 629.7 | 0.26x | 0.21x |
| 1 | 1MB | 966.8 | 1,204.0 | 1,274.1 | 0.80x | 0.76x |
| 10 | 1KB | 21.6 | 22.6 | 20.3 | 0.96x | **1.06x** |
| 10 | 100KB | 751.0 | 731.4 | 1,010.6 | **1.03x** | 0.74x |
| 10 | 1MB | 1,152.8 | 1,215.9 | 1,768.8 | 0.95x | 0.65x |
| 100 | 1KB | 37.8 | 30.7 | 25.4 | **1.23x** | **1.49x** |
| 100 | 100KB | 980.4 | 598.9 | 969.6 | **1.64x** | **1.01x** |
| 100 | 1MB | 921.4 | 1,158.4 | 1,794.4 | 0.80x | 0.51x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,545,353 | 86,981 | 119,010 | **17.77x** | **12.99x** |
| 1 | 256B | 1,224,526 | 86,895 | 115,359 | **14.09x** | **10.61x** |
| 10 | 64B | 1,145,697 | 98,414 | 126,410 | **11.64x** | **9.06x** |
| 10 | 256B | 833,527 | 103,570 | 121,220 | **8.05x** | **6.88x** |
| 50 | 64B | 1,123,965 | 105,954 | 126,910 | **10.61x** | **8.86x** |
| 50 | 256B | 863,508 | 104,984 | 124,650 | **8.23x** | **6.93x** |
| 1000 | 64B | 1,141,511 | 109,705 | 107,583 | **10.41x** | **10.61x** |
| 1000 | 256B | 954,321 | 277,140 | 104,908 | **3.44x** | **9.10x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 6/18 throughput comparisons against Go multiplexers.
At 100 channels with 1KB and 100KB payloads, NetConduit beats both FRP/Yamux (1.23–1.64x)
and Smux (1.01–1.49x). Single-channel 100KB shows a gap (0.21–0.26x) due to credit
round-trip latency dominating short transfers. At 1MB, NetConduit reaches 966.8 MB/s
(0.76–0.80x of Go muxes).

**Game-tick messaging:** NetConduit wins 16/16 comparisons against Go multiplexers,
reaching **3.4–17.8x faster than FRP/Yamux** and **6.9–13.0x faster than Smux**.
At 1 channel with 64B messages, NetConduit delivers 1,545,353 msg/s — nearly raw TCP
speed (1,569,582 msg/s). Performance stays above 833K msg/s even at 1000 channels
with 256B messages.

**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,
priority queuing ensures critical channels aren't starved, and adaptive windowing
automatically reclaims memory from idle channels. These features add measurable
overhead in single-channel bulk transfer but provide production safety guarantees
that simpler muxes don't offer — while dominating game-tick / real-time messaging
and beating Go muxes at scale (100 channels).

---

## Raw TCP Baselines

Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.
This is the theoretical ceiling, not a practical alternative (connection limits,
no flow control, no channel management).

### Throughput: All Implementations (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|-----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 1KB | 8.3 | 8.4 | 9.9 | 4.2 | 8.1 |
| 1 | 100KB | 631.9 | 516.8 | 629.7 | 294.1 | 132.6 |
| 1 | 1MB | 2,552.1 | 1,204.0 | 1,274.1 | 1,531.9 | 966.8 |
| 10 | 1KB | 14.8 | 22.6 | 20.3 | 8.2 | 21.6 |
| 10 | 100KB | 1,051.4 | 731.4 | 1,010.6 | 799.5 | 751.0 |
| 10 | 1MB | 2,978.6 | 1,215.9 | 1,768.8 | 2,629.8 | 1,152.8 |
| 100 | 1KB | 13.5 | 30.7 | 25.4 | 11.4 | 37.8 |
| 100 | 100KB | 877.3 | 598.9 | 969.6 | 854.1 | 980.4 |
| 100 | 1MB | 3,147.0 | 1,158.4 | 1,794.4 | 1,912.7 | 921.4 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 217,234 | 86,981 | 119,010 | 1,569,582 | 1,545,353 |
| 1 | 256B | 242,392 | 86,895 | 115,359 | 1,358,788 | 1,224,526 |
| 10 | 64B | 1,276,716 | 98,414 | 126,410 | 1,579,100 | 1,145,697 |
| 10 | 256B | 1,428,223 | 103,570 | 121,220 | 1,434,407 | 833,527 |
| 50 | 64B | 1,612,284 | 105,954 | 126,910 | 2,167,915 | 1,123,965 |
| 50 | 256B | 1,569,689 | 104,984 | 124,650 | — | 863,508 |
| 1000 | 64B | 1,667,558 | 109,705 | 107,583 | 2,592,601 | 1,141,511 |
| 1000 | 256B | 1,581,526 | 277,140 | 104,908 | 2,247,352 | 954,321 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.5x | 1.0x | 0.8x |
| 1 | 100KB | 2.2x | 1.2x | 1.0x |
| 1 | 1MB | 1.6x | 2.1x | 2.0x |
| 10 | 1KB | 0.4x | 0.7x | 0.7x |
| 10 | 100KB | 1.1x | 1.4x | 1.0x |
| 10 | 1MB | 2.3x | 2.4x | 1.7x |
| 100 | 1KB | 0.3x | 0.4x | 0.5x |
| 100 | 100KB | 0.9x | 1.5x | 0.9x |
| 100 | 1MB | 2.1x | 2.7x | 1.8x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.5x | 1.8x |
| 1 | 256B | 1.1x | 2.8x | 2.1x |
| 10 | 64B | 1.4x | 13.0x | 10.1x |
| 10 | 256B | 1.7x | 13.8x | 11.8x |
| 50 | 64B | 1.9x | 15.2x | 12.7x |
| 50 | 256B | 1.8x | 15.0x | 12.6x |
| 1000 | 64B | 2.3x | 15.2x | 15.5x |
| 1000 | 256B | 2.4x | 5.7x | 15.1x |

---

## Internal .NET Benchmarks (BenchmarkDotNet)

Measured with BenchmarkDotNet v0.15.8, InProcess toolchain, ShortRun (3 iterations).
These measure multiplexer overhead vs raw TCP within the same .NET runtime.

**Environment:** Linux Ubuntu 22.04.5, Intel Xeon Gold 5218R 2.10GHz, 16 physical cores,
.NET 10.0.5, RyuJIT x86-64-v4.

### Mux Overhead vs Raw TCP (Time)

Ratio = Mux Mean / Raw TCP Mean. Lower = less overhead.

| Channels | Data Size | Raw TCP (ms) | Mux TCP (ms) | Ratio | Mux Allocated |
|----------|-----------|-------------:|-------------:|------:|--------------:|
| 1 | 1KB | 51.5 | 52.4 | 1.02x | 3.1 MB |
| 1 | 100KB | 51.4 | 53.3 | 1.04x | 2.2 MB |
| 1 | 1MB | 53.0 | 55.3 | 1.04x | 2.2 MB |
| 10 | 1KB | 51.6 | 58.1 | 1.13x | 12.8 MB |
| 10 | 100KB | 51.8 | 58.4 | 1.13x | 12.8 MB |
| 10 | 1MB | 54.2 | 63.5 | 1.17x | 11.9 MB |
| 100 | 1KB | 54.9 | 77.9 | 1.42x | 108.9 MB |
| 100 | 100KB | 57.8 | 86.3 | 1.49x | 108.9 MB |
| 100 | 1MB | 66.4 | 137.8 | 2.08x | 108.6 MB |
| 1000 | 1KB | 88.0 | 227.3 | 2.58x | 1070.6 MB |
| 1000 | 100KB | 93.2 | 287.9 | 3.09x | 1069.7 MB |
| 1000 | 1MB | 173.9 | 889.3 | 5.11x | 1077.2 MB |

### Key Observations

**Low channel counts (1–10):** Mux overhead is 2–17% over raw TCP.
The multiplexer adds minimal latency for typical use cases with a few channels.

**Medium channel counts (100):** Overhead grows to 1.4–2.1x due to channel management,
flow control credits, and per-channel state. Still practical for most applications.

**High channel counts (1000):** Overhead reaches 2.6–5.1x. This is expected — managing
1000 concurrent channels with credit-based flow control, priority queuing, and
adaptive windowing has inherent cost. Raw TCP at 1000 connections is also impractical
in production (file descriptor limits, connection overhead).

**Memory:** Mux allocates ~1 MB per channel for initial state (buffers, credit tracking,
frame encoding). At 1000 channels, this becomes substantial (~1 GB). Memory profile
is dominated by per-channel buffer allocation and is independent of data size.

---

*Generated by NetConduit comparison benchmark suite and BenchmarkDotNet internal suite*
