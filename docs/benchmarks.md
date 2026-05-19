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
| 1 | 64 B | 987,139 | 238,304 | 293,034 | **4.14x** | **3.37x** |
| 1 | 256 B | 821,175 | 216,285 | 290,415 | **3.80x** | **2.83x** |
| 10 | 64 B | 844,347 | 245,434 | 278,414 | **3.44x** | **3.03x** |
| 10 | 256 B | 678,706 | 234,312 | 268,734 | **2.90x** | **2.53x** |
| 50 | 64 B | 1,045,889 | 247,396 | 257,750 | **4.23x** | **4.06x** |
| 50 | 256 B | 846,521 | 229,358 | 267,778 | **3.69x** | **3.16x** |
| 1000 | 64 B | 4,258,558 | 283,587 | 216,788 | **15.02x** | **19.64x** |
| 1000 | 256 B | 2,620,660 | 411,346 | 233,666 | **6.37x** | **11.22x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 8/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 12.0 | 9.3 | 9.3 | **1.29x** | **1.29x** |
| 1 | 100 KB | 618.5 | 337.1 | 780.7 | **1.83x** | 0.79x |
| 1 | 1 MB | 1,190.9 | 1,367.9 | 2,996.9 | 0.87x | 0.40x |
| 10 | 1 KB | 30.6 | 40.6 | 42.1 | 0.75x | 0.73x |
| 10 | 100 KB | 1,443.8 | 897.7 | 1,931.8 | **1.61x** | 0.75x |
| 10 | 1 MB | 2,112.2 | 2,051.9 | 4,350.3 | **1.03x** | 0.49x |
| 100 | 1 KB | 84.1 | 52.3 | 42.7 | **1.61x** | **1.97x** |
| 100 | 100 KB | 2,156.0 | 1,086.2 | 2,315.3 | **1.98x** | 0.93x |
| 100 | 1 MB | 1,647.1 | 2,421.2 | 3,823.4 | 0.68x | 0.43x |

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