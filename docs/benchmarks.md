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
| 1 | 64 B | 1,035,687 | 254,109 | 323,716 | **4.08x** | **3.20x** |
| 1 | 256 B | 816,748 | 235,140 | 340,159 | **3.47x** | **2.40x** |
| 10 | 64 B | 1,170,486 | 257,737 | 290,138 | **4.54x** | **4.03x** |
| 10 | 256 B | 981,624 | 268,116 | 313,962 | **3.66x** | **3.13x** |
| 50 | 64 B | 1,447,782 | 288,408 | 315,532 | **5.02x** | **4.59x** |
| 50 | 256 B | 1,002,666 | 280,994 | 283,986 | **3.57x** | **3.53x** |
| 1000 | 64 B | 4,466,851 | 321,597 | 243,060 | **13.89x** | **18.38x** |
| 1000 | 256 B | 2,556,083 | 471,839 | 243,974 | **5.42x** | **10.48x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 6/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 3.3 | 9.5 | 9.1 | 0.35x | 0.36x |
| 1 | 100 KB | 379.4 | 479.6 | 985.0 | 0.79x | 0.39x |
| 1 | 1 MB | 696.4 | 1,762.4 | 2,460.2 | 0.40x | 0.28x |
| 10 | 1 KB | 23.2 | 41.0 | 33.7 | 0.56x | 0.69x |
| 10 | 100 KB | 1,654.6 | 1,021.8 | 1,617.3 | **1.62x** | **1.02x** |
| 10 | 1 MB | 1,997.2 | 2,177.4 | 3,841.7 | 0.92x | 0.52x |
| 100 | 1 KB | 142.0 | 69.6 | 45.5 | **2.04x** | **3.12x** |
| 100 | 100 KB | 1,895.2 | 1,261.6 | 2,433.5 | **1.50x** | 0.78x |
| 100 | 1 MB | 2,339.0 | 1,977.7 | 3,687.5 | **1.18x** | 0.63x |

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