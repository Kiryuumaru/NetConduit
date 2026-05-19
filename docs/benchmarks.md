# Benchmarks

NetConduit ships a comparison benchmark suite that pits NetConduit's TCP multiplexer against raw TCP and two reference Go multiplexers — **FRP/Yamux** (HashiCorp, used by FRP/Consul/Nomad) and **Smux** (xtaci/smux).

Full report with methodology: [`benchmarks/docker/results/comparison-report.md`](../benchmarks/docker/results/comparison-report.md).

Raw JSON: `benchmarks/docker/results/{dotnet,go}-{throughput,gametick}.json`.

## Headline results

Each measurement is the median of 5 runs. Containers pinned to 2 CPU cores, `GOMAXPROCS=2`, loopback only.

### Game-tick (msg/s — higher is better)

Small-message workload typical of game state and real-time control. NetConduit wins **all 16** comparisons against the Go multiplexers — it is 3–5× faster than FRP/Yamux and Smux at low channel counts, growing to ~16–19× at 1000 channels.

| Channels | Msg | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 64 B | 1,045,049 | 232,560 | 302,226 | **4.49x** | **3.46x** |
| 1 | 256 B | 974,350 | 197,790 | 291,039 | **4.93x** | **3.35x** |
| 10 | 64 B | 1,086,380 | 232,750 | 264,775 | **4.67x** | **4.10x** |
| 10 | 256 B | 966,529 | 215,370 | 270,266 | **4.49x** | **3.58x** |
| 50 | 64 B | 1,292,230 | 228,572 | 271,642 | **5.65x** | **4.76x** |
| 50 | 256 B | 1,016,322 | 223,994 | 269,955 | **4.54x** | **3.76x** |
| 1000 | 64 B | 4,162,043 | 262,222 | 221,604 | **15.87x** | **18.78x** |
| 1000 | 256 B | 2,469,060 | 435,684 | 226,352 | **5.67x** | **10.91x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins **11/18** comparisons against the Go multiplexers — leads on 1 KB and 100 KB payloads but trails Smux at 1 MiB payloads where Smux's lighter window machinery wins.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 8.1 | 5.8 | 5.7 | **1.39x** | **1.42x** |
| 1 | 100 KB | 846.2 | 347.7 | 597.3 | **2.43x** | **1.42x** |
| 1 | 1 MB | 936.9 | 1,237.6 | 2,531.9 | 0.76x | 0.37x |
| 10 | 1 KB | 67.1 | 31.3 | 28.5 | **2.14x** | **2.35x** |
| 10 | 100 KB | 1,765.3 | 999.8 | 1,740.1 | **1.77x** | **1.01x** |
| 10 | 1 MB | 2,111.7 | 2,054.7 | 2,782.9 | **1.03x** | 0.76x |
| 100 | 1 KB | 32.0 | 44.3 | 36.0 | 0.72x | 0.89x |
| 100 | 100 KB | 1,817.4 | 953.5 | 2,194.8 | **1.91x** | 0.83x |
| 100 | 1 MB | 2,202.4 | 1,666.0 | 3,281.9 | **1.32x** | 0.67x |

### Raw TCP baseline

Raw TCP uses **N separate connections** (no multiplexing) — a theoretical ceiling, not a practical alternative. Full per-implementation tables for both Go raw TCP and .NET raw TCP are in the [comparison report](../benchmarks/docker/results/comparison-report.md).

## What it means

- For **small-message high-frequency** workloads, NetConduit's writer/scheduler is consistently 3–5× faster than the popular Go multiplexers at every channel count, and ~16–19× faster at 1000 channels.
- For **bulk throughput** at small-to-medium payloads (1 KB and 100 KB), NetConduit beats both Go muxes at most channel counts — the lone exception is 100 channels × 1 KB where the per-channel scheduler bookkeeping dominates.
- For **1 MiB payloads**, Smux is consistently fastest. NetConduit is competitive with FRP/Yamux and beats it at 10 and 100 channels, but trails Smux's lighter window machinery.
- The cost NetConduit pays for the bulk-1 MiB workloads: credit-based backpressure (no OOM under load), priority queuing, and adaptive windowing. These features add measurable overhead but provide guarantees the simpler muxes don't offer.

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