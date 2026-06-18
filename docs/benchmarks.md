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
| 1 | 64 B | 1,080,035 | 244,788 | 317,307 | **4.41x** | **3.40x** |
| 1 | 256 B | 836,999 | 216,393 | 307,518 | **3.87x** | **2.72x** |
| 10 | 64 B | 1,117,928 | 249,163 | 290,757 | **4.49x** | **3.84x** |
| 10 | 256 B | 831,057 | 249,476 | 298,134 | **3.33x** | **2.79x** |
| 50 | 64 B | 1,335,018 | 262,596 | 284,688 | **5.08x** | **4.69x** |
| 50 | 256 B | 924,202 | 255,236 | 269,837 | **3.62x** | **3.43x** |
| 1000 | 64 B | 4,278,537 | 342,396 | 242,615 | **12.50x** | **17.64x** |
| 1000 | 256 B | 2,648,609 | 508,052 | 244,469 | **5.21x** | **10.83x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 11/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 10.8 | 8.2 | 7.6 | **1.33x** | **1.43x** |
| 1 | 100 KB | 707.7 | 371.0 | 801.4 | **1.91x** | 0.88x |
| 1 | 1 MB | 1,455.8 | 1,348.7 | 2,825.5 | **1.08x** | 0.52x |
| 10 | 1 KB | 66.7 | 41.7 | 34.7 | **1.60x** | **1.92x** |
| 10 | 100 KB | 1,169.8 | 817.9 | 1,485.1 | **1.43x** | 0.79x |
| 10 | 1 MB | 1,862.0 | 1,970.3 | 3,194.1 | 0.94x | 0.58x |
| 100 | 1 KB | 144.8 | 41.4 | 47.3 | **3.50x** | **3.06x** |
| 100 | 100 KB | 2,505.5 | 1,100.2 | 2,069.4 | **2.28x** | **1.21x** |
| 100 | 1 MB | 1,908.0 | 2,041.9 | 3,344.9 | 0.93x | 0.57x |

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