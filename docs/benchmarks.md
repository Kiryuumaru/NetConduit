# Benchmarks

NetConduit ships a comparison benchmark suite that pits NetConduit's TCP multiplexer against raw TCP and two reference Go multiplexers — **FRP/Yamux** (HashiCorp, used by FRP/Consul/Nomad) and **Smux** (xtaci/smux).

Full report with methodology: [`benchmarks/docker/results/comparison-report.md`](../benchmarks/docker/results/comparison-report.md).

Raw JSON: `benchmarks/docker/results/{dotnet,go}-{throughput,gametick}.json`.

## Headline results

Each measurement is the median of 5 runs. Containers pinned to 2 CPU cores, `GOMAXPROCS=2`, loopback only.

### Game-tick (msg/s — higher is better)

Small-message workload typical of game state and real-time control. **NetConduit wins all 16 comparisons against Go multiplexers.**

| Channels | Msg | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 64 B | 1,140,551 | 246,015 | 326,407 | **4.64x** | **3.49x** |
| 1 | 256 B | 1,147,881 | 212,599 | 336,030 | **5.40x** | **3.42x** |
| 10 | 64 B | 1,242,974 | 237,982 | 287,982 | **5.22x** | **4.32x** |
| 10 | 256 B | 1,176,395 | 246,564 | 286,267 | **4.77x** | **4.11x** |
| 50 | 64 B | 1,546,152 | 273,151 | 293,880 | **5.66x** | **5.26x** |
| 50 | 256 B | 1,232,333 | 262,566 | 280,286 | **4.69x** | **4.40x** |
| 1000 | 64 B | 4,759,378 | 264,416 | 239,971 | **18.00x** | **19.83x** |
| 1000 | 256 B | 2,686,055 | 370,360 | 246,764 | **7.25x** | **10.89x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 10/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 5.3 | 13.2 | 10.7 | 0.40x | 0.49x |
| 1 | 100 KB | 729.3 | 339.8 | 898.3 | **2.15x** | 0.81x |
| 1 | 1 MB | 1,198.5 | 1,621.9 | 2,664.8 | 0.74x | 0.45x |
| 10 | 1 KB | 100.0 | 49.1 | 42.5 | **2.04x** | **2.35x** |
| 10 | 100 KB | 2,308.1 | 937.5 | 1,731.4 | **2.46x** | **1.33x** |
| 10 | 1 MB | 2,854.3 | 2,980.1 | 4,081.4 | 0.96x | 0.70x |
| 100 | 1 KB | 58.2 | 50.9 | 58.0 | **1.14x** | **1.00x** |
| 100 | 100 KB | 2,287.4 | 1,288.4 | 2,268.5 | **1.78x** | **1.01x** |
| 100 | 1 MB | 2,659.9 | 2,438.3 | 4,132.4 | **1.09x** | 0.64x |

### Raw TCP baseline

Raw TCP uses **N separate connections** (no multiplexing) — a theoretical ceiling, not a practical alternative. Full per-implementation tables for both Go raw TCP and .NET raw TCP are in the [comparison report](../benchmarks/docker/results/comparison-report.md).

## What it means

- For **small-message high-frequency** workloads (game tick, RPC, control planes), NetConduit's writer/scheduler is much faster than the popular Go multiplexers.
- For **single bulk transfers** at large payloads, raw TCP is ahead of every multiplexer; among muxes, NetConduit is competitive and wins on small-to-medium payloads with multiple channels.
- The cost NetConduit pays: credit-based backpressure (no OOM under load), priority queuing, and adaptive windowing. These features add measurable overhead but provide guarantees the simpler muxes don't offer.

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