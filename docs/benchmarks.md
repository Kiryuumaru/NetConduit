# Benchmarks

NetConduit ships a comparison benchmark suite that pits NetConduit's TCP multiplexer against raw TCP and two reference Go multiplexers — **FRP/Yamux** (HashiCorp, used by FRP/Consul/Nomad) and **Smux** (xtaci/smux).

Full report with methodology: [`benchmarks/docker/results/comparison-report.md`](../benchmarks/docker/results/comparison-report.md).

Raw JSON: `benchmarks/docker/results/{dotnet,go}-{throughput,gametick}.json`.

## Headline results

Each measurement is the median of 5 runs. Containers pinned to 2 CPU cores, `GOMAXPROCS=2`, loopback only.

### Game-tick (msg/s — higher is better)

Small-message workload typical of game state and real-time control. NetConduit wins **6/16** comparisons against the Go multiplexers on this branch — it dominates at high channel counts but is slower than FRP/Yamux and Smux at low channel counts.

| Channels | Msg | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 64 B | 7,268 | 243,745 | 315,004 | 0.03x | 0.02x |
| 1 | 256 B | 1,986 | 225,840 | 312,944 | 0.01x | 0.01x |
| 10 | 64 B | 72,554 | 248,508 | 300,620 | 0.29x | 0.24x |
| 10 | 256 B | 19,832 | 266,034 | 294,112 | 0.07x | 0.07x |
| 50 | 64 B | 359,970 | 254,118 | 288,573 | **1.42x** | **1.25x** |
| 50 | 256 B | 99,065 | 258,808 | 278,130 | 0.38x | 0.36x |
| 1000 | 64 B | 3,967,441 | 327,233 | 248,014 | **12.12x** | **16.00x** |
| 1000 | 256 B | 1,906,238 | 491,072 | 250,586 | **3.88x** | **7.61x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. Of the 6 channel/size combinations that completed without error, NetConduit wins **12/12** head-to-head comparisons against FRP/Yamux and Smux. NetConduit currently fails the 1 MiB payload configurations on this branch with `TimeoutException` — those rows are reported as `FAILED` and excluded from the win count.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 10.9 | 7.5 | 7.6 | **1.46x** | **1.44x** |
| 1 | 100 KB | 781.2 | 332.7 | 489.5 | **2.35x** | **1.60x** |
| 1 | 1 MB | FAILED | 1,250.4 | 2,725.4 | — | — |
| 10 | 1 KB | 51.7 | 34.8 | 36.9 | **1.48x** | **1.40x** |
| 10 | 100 KB | 1,731.5 | 921.3 | 1,640.5 | **1.88x** | **1.06x** |
| 10 | 1 MB | FAILED | 1,771.4 | 3,314.8 | — | — |
| 100 | 1 KB | 130.7 | 43.0 | 53.2 | **3.04x** | **2.46x** |
| 100 | 100 KB | 2,857.7 | 1,194.2 | 2,425.3 | **2.39x** | **1.18x** |
| 100 | 1 MB | FAILED | 2,273.8 | 3,734.5 | — | — |

### Raw TCP baseline

Raw TCP uses **N separate connections** (no multiplexing) — a theoretical ceiling, not a practical alternative. Full per-implementation tables for both Go raw TCP and .NET raw TCP are in the [comparison report](../benchmarks/docker/results/comparison-report.md).

## What it means

- For **small-message high-frequency** workloads with many channels (≥50 channels at 64 B, or ≥1000 channels at any size), NetConduit's writer/scheduler is markedly faster than the popular Go multiplexers. At 1000 channels NetConduit reaches ~3.97 M msg/s vs ~0.33 M for FRP/Yamux and ~0.25 M for Smux.
- For **low channel counts** (1–10) and for 50 channels at 256 B, NetConduit is currently **slower** than FRP/Yamux and Smux. The credit/backpressure machinery has a per-message overhead that dominates when there are few concurrent channels to amortise it over.
- For **bulk throughput** at 1 KB and 100 KB payloads, NetConduit beats both Go muxes across every measured channel count (12/12 head-to-head wins).
- The **1 MiB throughput configurations currently fail** with `TimeoutException` on this branch; treat that as a known regression rather than a deliberate result. The Go muxes complete all 1 MiB configurations and Smux is the fastest there (up to ~3.7 GB/s at 100 channels).
- The cost NetConduit pays for the workloads where it loses: credit-based backpressure (no OOM under load), priority queuing, and adaptive windowing. These features add measurable overhead but provide guarantees the simpler muxes don't offer.

## What is benchmarked

The project at [`benchmarks/docker/netconduit-comparison`](../benchmarks/docker/netconduit-comparison/) runs two workloads:

| Workload | Measures |
| --- | --- |
| `throughput` | Sustained MB/sec across 1 / 10 / 100 channels at 1 KiB / 100 KiB / 1 MiB payloads. |
| `game-tick` | Messages/sec under a tight tick loop across 1 / 10 / 50 / 1000 channels at 64 / 256 byte payloads. |

Two implementations on the .NET side: bare `TcpClient` / `TcpListener` and `StreamMultiplexer` over `NetConduit.Transport.Tcp`. The Go side (`benchmarks/docker/go-comparison`) runs raw TCP, FRP/Yamux, and Smux.

## Running

The full suite in a controlled Docker environment with CPU affinity and `GOMAXPROCS` pinned:

```bash
./benchmarks/docker/run-docker.sh
```

A faster local run that skips Docker:

```bash
./benchmarks/docker/run-quick.sh
```

Or run the .NET side directly:

```bash
dotnet run --project benchmarks/docker/netconduit-comparison -c Release -- throughput
dotnet run --project benchmarks/docker/netconduit-comparison -c Release -- game-tick
```

Without arguments both workloads run.

## Output files

`run-benchmarks.sh` writes:

- `benchmarks/docker/results/dotnet-throughput.json`
- `benchmarks/docker/results/dotnet-gametick.json`
- `benchmarks/docker/results/go-throughput.json`
- `benchmarks/docker/results/go-gametick.json`

`report.py` then folds those into [`benchmarks/docker/results/comparison-report.md`](../benchmarks/docker/results/comparison-report.md).

Legacy BenchmarkDotNet artifacts from older runs are kept under [`BenchmarkDotNet.Artifacts/results/`](../BenchmarkDotNet.Artifacts/results/) for reference.

## Tuning hints

| Goal | Knob |
| --- | --- |
| Maximum single-channel throughput | `ChannelOptions.SlabSize = 4..16 MiB` |
| Critical small messages | Raise `ChannelOptions.Priority` |
| Low latency | `TcpClient.NoDelay = true` in a custom `StreamFactory` |
| High channel count | Default 1 MiB slab keeps memory bounded at `count * 1 MiB` |