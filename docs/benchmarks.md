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
| 1 | 64 B | 984,174 | 223,522 | 281,732 | **4.40x** | **3.49x** |
| 1 | 256 B | 783,031 | 193,252 | 290,464 | **4.05x** | **2.70x** |
| 10 | 64 B | 1,055,823 | 276,442 | 288,752 | **3.82x** | **3.66x** |
| 10 | 256 B | 854,121 | 246,478 | 264,472 | **3.47x** | **3.23x** |
| 50 | 64 B | 1,310,109 | 240,453 | 254,334 | **5.45x** | **5.15x** |
| 50 | 256 B | 911,868 | 240,260 | 252,897 | **3.80x** | **3.61x** |
| 1000 | 64 B | 4,082,784 | 318,058 | 209,817 | **12.84x** | **19.46x** |
| 1000 | 256 B | 2,507,965 | 294,756 | 231,760 | **8.51x** | **10.82x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 10/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 4.4 | 6.2 | 6.3 | 0.71x | 0.69x |
| 1 | 100 KB | 461.1 | 210.9 | 412.8 | **2.19x** | **1.12x** |
| 1 | 1 MB | 1,151.9 | 926.7 | 1,803.9 | **1.24x** | 0.64x |
| 10 | 1 KB | 43.0 | 33.2 | 25.7 | **1.29x** | **1.68x** |
| 10 | 100 KB | 1,245.0 | 825.7 | 1,541.8 | **1.51x** | 0.81x |
| 10 | 1 MB | 1,528.6 | 1,637.3 | 3,104.6 | 0.93x | 0.49x |
| 100 | 1 KB | 62.1 | 46.8 | 37.9 | **1.33x** | **1.64x** |
| 100 | 100 KB | 1,482.0 | 1,009.2 | 1,991.3 | **1.47x** | 0.74x |
| 100 | 1 MB | 1,998.3 | 1,821.6 | 3,348.7 | **1.10x** | 0.60x |

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