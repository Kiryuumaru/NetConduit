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
| 1 | 1KB | 8.1 | 9.4 | 11.0 | 0.87x | 0.74x |
| 1 | 100KB | 347.5 | 484.5 | 605.0 | 0.72x | 0.57x |
| 1 | 1MB | 692.8 | 1,017.8 | 1,293.3 | 0.68x | 0.54x |
| 10 | 1KB | 27.5 | 23.1 | 18.9 | **1.19x** | **1.45x** |
| 10 | 100KB | 759.8 | 612.8 | 803.1 | **1.24x** | 0.95x |
| 10 | 1MB | 1,291.1 | 1,085.7 | 1,755.3 | **1.19x** | 0.74x |
| 100 | 1KB | 29.1 | 23.0 | 20.6 | **1.27x** | **1.41x** |
| 100 | 100KB | 859.0 | 701.6 | 1,054.0 | **1.22x** | 0.82x |
| 100 | 1MB | 1,114.7 | 1,244.4 | 1,738.0 | 0.90x | 0.64x |

### Game-Tick Message Rate (msg/s)

Each channel sends many small messages (simulates game state updates). Higher = better.

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
|----------|----------|----------:|----------:|-----:|----------:|----------:|
| 1 | 64B | 1,603,176 | 86,506 | 114,889 | **18.53x** | **13.95x** |
| 1 | 256B | 1,183,454 | 85,117 | 120,433 | **13.90x** | **9.83x** |
| 10 | 64B | 1,199,841 | 106,564 | 132,258 | **11.26x** | **9.07x** |
| 10 | 256B | 840,004 | 103,586 | 132,380 | **8.11x** | **6.35x** |
| 50 | 64B | 1,172,894 | 104,324 | 125,788 | **11.24x** | **9.32x** |
| 50 | 256B | 832,948 | 107,740 | 128,823 | **7.73x** | **6.47x** |
| 1000 | 64B | 1,150,963 | 109,079 | 107,045 | **10.55x** | **10.75x** |
| 1000 | 256B | 848,187 | 117,278 | 108,764 | **7.23x** | **7.80x** |

---

## Key Takeaways

**Bulk throughput:** NetConduit wins 7/18 throughput comparisons against Go multiplexers.
At 10+ channels with small payloads (1KB), NetConduit beats both FRP/Yamux (1.19–1.27x)
and Smux (1.41–1.45x). At 10–100 channels with 100KB payloads, NetConduit beats FRP/Yamux
(1.22–1.24x). Single-channel shows a gap (0.54–0.87x) due to credit round-trip latency
dominating short transfers. At 100 channels 1MB, NetConduit reaches 1,114.7 MB/s (0.90x
of FRP/Yamux).

**Game-tick messaging:** NetConduit wins 16/16 comparisons against Go multiplexers,
reaching **7.2–18.5x faster than FRP/Yamux** and **6.3–14.0x faster than Smux**.
At 1 channel with 64B messages, NetConduit delivers 1,603,176 msg/s — near raw TCP
speed. Performance stays above 832K msg/s even at 1000 channels with 256B messages.

**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,
priority queuing ensures critical channels aren't starved, and adaptive windowing
automatically reclaims memory from idle channels. These features add measurable
overhead in single-channel bulk transfer but provide production safety guarantees
that simpler muxes don't offer — while dominating game-tick / real-time messaging
and beating Go muxes at scale (10–100 channels).

---

## Raw TCP Baselines

Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.
This is the theoretical ceiling, not a practical alternative (connection limits,
no flow control, no channel management).

### Throughput: All Implementations (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|-----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 1KB | 7.9 | 9.4 | 11.0 | 1.9 | 8.1 |
| 1 | 100KB | 580.5 | 484.5 | 605.0 | 384.5 | 347.5 |
| 1 | 1MB | 2,420.2 | 1,017.8 | 1,293.3 | 1,982.6 | 692.8 |
| 10 | 1KB | 15.4 | 23.1 | 18.9 | 9.1 | 27.5 |
| 10 | 100KB | 1,088.2 | 612.8 | 803.1 | 688.8 | 759.8 |
| 10 | 1MB | 3,269.2 | 1,085.7 | 1,755.3 | 2,579.2 | 1,291.1 |
| 100 | 1KB | 12.9 | 23.0 | 20.6 | 11.6 | 29.1 |
| 100 | 100KB | 1,046.5 | 701.6 | 1,054.0 | 507.8 | 859.0 |
| 100 | 1MB | 3,259.4 | 1,244.4 | 1,738.0 | 1,788.0 | 1,114.7 |

### Game-Tick: All Implementations (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |
|----------|----------|----------:|----------:|----------:|----------:|----------:|
| 1 | 64B | 209,080 | 86,506 | 114,889 | 1,545,262 | 1,603,176 |
| 1 | 256B | 238,889 | 85,117 | 120,433 | 1,363,667 | 1,183,454 |
| 10 | 64B | 1,327,210 | 106,564 | 132,258 | 2,046,427 | 1,199,841 |
| 10 | 256B | 1,443,900 | 103,586 | 132,380 | 1,379,750 | 840,004 |
| 50 | 64B | 1,648,778 | 104,324 | 125,788 | 2,034,498 | 1,172,894 |
| 50 | 256B | 1,544,985 | 107,740 | 128,823 | 1,892,524 | 832,948 |
| 1000 | 64B | 1,661,790 | 109,079 | 107,045 | 2,467,335 | 1,150,963 |
| 1000 | 256B | 1,590,780 | 117,278 | 108,764 | 2,229,076 | 848,187 |

---

## Multiplexer Overhead vs Raw TCP

How much does multiplexing cost compared to raw connections?
All multiplexers share this overhead — it's inherent to running N streams over 1 connection.

### Throughput Overhead

Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).

| Channels | Data Size | NetConduit | FRP/Yamux | Smux |
|----------|-----------|----------:|----------:|----------:|
| 1 | 1KB | 0.2x | 0.8x | 0.7x |
| 1 | 100KB | 1.1x | 1.2x | 1.0x |
| 1 | 1MB | 2.9x | 2.4x | 1.9x |
| 10 | 1KB | 0.3x | 0.7x | 0.8x |
| 10 | 100KB | 0.9x | 1.8x | 1.4x |
| 10 | 1MB | 2.0x | 3.0x | 1.9x |
| 100 | 1KB | 0.4x | 0.6x | 0.6x |
| 100 | 100KB | 0.6x | 1.5x | 1.0x |
| 100 | 1MB | 1.6x | 2.6x | 1.9x |

### Game-Tick Overhead

Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).

| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |
|----------|----------|----------:|----------:|----------:|
| 1 | 64B | 1.0x | 2.4x | 1.8x |
| 1 | 256B | 1.2x | 2.8x | 2.0x |
| 10 | 64B | 1.7x | 12.5x | 10.0x |
| 10 | 256B | 1.6x | 13.9x | 10.9x |
| 50 | 64B | 1.7x | 15.8x | 13.1x |
| 50 | 256B | 2.3x | 14.3x | 12.0x |
| 1000 | 64B | 2.1x | 15.2x | 15.5x |
| 1000 | 256B | 2.6x | 13.6x | 14.6x |

---

## Internal .NET Benchmarks (BenchmarkDotNet)

Measured with BenchmarkDotNet v0.15.8, InProcess toolchain, ShortRun (3 iterations),
median of 3 independent runs.
These measure multiplexer overhead vs raw TCP within the same .NET runtime.

**Environment:** Linux Ubuntu 22.04.5, Intel Xeon Gold 5218R 2.10GHz, 16 physical cores,
.NET 10.0.5, RyuJIT x86-64-v4.

### Mux Overhead vs Raw TCP (Time)

Ratio = Mux Mean / Raw TCP Mean. Lower = less overhead.

| Channels | Data Size | Raw TCP (ms) | Mux TCP (ms) | Ratio | Mux Allocated |
|----------|-----------|-------------:|-------------:|------:|--------------:|
| 1 | 1KB | 51.6 | 56.0 | 1.09x | 1.1 MB |
| 1 | 100KB | 51.6 | 54.0 | 1.05x | 2.2 MB |
| 1 | 1MB | 53.1 | 69.4 | 1.31x | 2.3 MB |
| 10 | 1KB | 52.0 | 55.7 | 1.07x | 1.1 MB |
| 10 | 100KB | 51.9 | 56.4 | 1.09x | 3.1 MB |
| 10 | 1MB | 56.0 | 71.5 | 1.28x | 11.4 MB |
| 100 | 1KB | 54.2 | 72.7 | 1.34x | 2.7 MB |
| 100 | 100KB | 54.5 | 87.0 | 1.60x | 11.4 MB |
| 100 | 1MB | 87.8 | 159.4 | 1.81x | 104.4 MB |
| 1000 | 1KB | 71.4 | 154.2 | 2.16x | 7.7 MB |
| 1000 | 100KB | 118.5 | 289.3 | 2.44x | 107.4 MB |
| 1000 | 1MB | 311.0 | 1,246.9 | 4.01x | 1,032.5 MB |

### Key Observations

**Low channel counts (1–10):** Mux overhead is 5–31% over raw TCP.
Small payloads (1KB–100KB) add minimal latency; 1MB single-channel shows
higher overhead due to credit round-trip costs on the single stream.

**Medium channel counts (100):** Overhead grows to 1.3–1.8x due to channel management,
flow control credits, and per-channel state. Still practical for most applications.

**High channel counts (1000):** Overhead reaches 2.2–4.0x. This is expected — managing
1000 concurrent channels with credit-based flow control, priority queuing, and
adaptive windowing has inherent cost. Raw TCP at 1000 connections is also impractical
in production (file descriptor limits, connection overhead).

**Memory:** Mux allocates per-channel state for buffers, credit tracking, and
frame encoding. At 1000 channels with 1MB data, total allocation reaches ~1 GB.
Memory profile scales with both channel count and data size.

---

*Generated by NetConduit comparison benchmark suite and BenchmarkDotNet internal suite*
