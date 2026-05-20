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
| 1 | 64 B | 1,077,404 | 223,686 | 290,714 | **4.82x** | **3.71x** |
| 1 | 256 B | 899,690 | 209,745 | 312,957 | **4.29x** | **2.87x** |
| 10 | 64 B | 1,181,620 | 249,077 | 289,805 | **4.74x** | **4.08x** |
| 10 | 256 B | 900,995 | 240,971 | 270,887 | **3.74x** | **3.33x** |
| 50 | 64 B | 1,433,225 | 239,114 | 278,616 | **5.99x** | **5.14x** |
| 50 | 256 B | 997,521 | 259,510 | 295,642 | **3.84x** | **3.37x** |
| 1000 | 64 B | 4,113,140 | 300,088 | 254,160 | **13.71x** | **16.18x** |
| 1000 | 256 B | 2,522,902 | 484,079 | 245,792 | **5.21x** | **10.26x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 10/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 10.1 | 9.3 | 10.0 | **1.09x** | **1.01x** |
| 1 | 100 KB | 836.8 | 381.8 | 906.0 | **2.19x** | 0.92x |
| 1 | 1 MB | 1,201.9 | 1,555.6 | 3,354.4 | 0.77x | 0.36x |
| 10 | 1 KB | 54.6 | 37.7 | 37.4 | **1.45x** | **1.46x** |
| 10 | 100 KB | 1,562.5 | 1,126.8 | 1,818.8 | **1.39x** | 0.86x |
| 10 | 1 MB | 2,684.1 | 2,169.5 | 3,142.1 | **1.24x** | 0.85x |
| 100 | 1 KB | 89.3 | 35.5 | 39.9 | **2.52x** | **2.24x** |
| 100 | 100 KB | 1,641.6 | 1,154.6 | 2,388.9 | **1.42x** | 0.69x |
| 100 | 1 MB | 2,064.2 | 2,160.8 | 3,748.4 | 0.96x | 0.55x |

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