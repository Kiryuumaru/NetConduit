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
| 1 | 64 B | 930,051 | 351,194 | 293,188 | **2.65x** | **3.17x** |
| 1 | 256 B | 799,239 | 193,536 | 285,792 | **4.13x** | **2.80x** |
| 10 | 64 B | 1,033,646 | 211,180 | 275,746 | **4.89x** | **3.75x** |
| 10 | 256 B | 782,273 | 220,450 | 253,201 | **3.55x** | **3.09x** |
| 50 | 64 B | 1,203,728 | 239,860 | 256,436 | **5.02x** | **4.69x** |
| 50 | 256 B | 846,811 | 232,512 | 250,835 | **3.64x** | **3.38x** |
| 1000 | 64 B | 4,184,497 | 270,917 | 212,524 | **15.45x** | **19.69x** |
| 1000 | 256 B | 2,524,669 | 483,099 | 219,942 | **5.23x** | **11.48x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 10/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 8.3 | 6.8 | 7.1 | **1.23x** | **1.17x** |
| 1 | 100 KB | 421.1 | 290.6 | 650.2 | **1.45x** | 0.65x |
| 1 | 1 MB | 992.3 | 1,265.6 | 2,269.0 | 0.78x | 0.44x |
| 10 | 1 KB | 28.7 | 34.9 | 26.1 | 0.82x | **1.10x** |
| 10 | 100 KB | 1,331.2 | 765.5 | 1,473.3 | **1.74x** | 0.90x |
| 10 | 1 MB | 1,742.1 | 1,660.0 | 2,967.1 | **1.05x** | 0.59x |
| 100 | 1 KB | 88.3 | 46.0 | 46.4 | **1.92x** | **1.90x** |
| 100 | 100 KB | 1,974.9 | 911.0 | 2,270.6 | **2.17x** | 0.87x |
| 100 | 1 MB | 1,808.6 | 1,580.3 | 3,014.2 | **1.14x** | 0.60x |

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