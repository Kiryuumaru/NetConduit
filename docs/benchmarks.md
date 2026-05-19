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
| 1 | 64 B | 1,050,545 | 219,349 | 286,056 | **4.79x** | **3.67x** |
| 1 | 256 B | 793,592 | 192,422 | 266,478 | **4.12x** | **2.98x** |
| 10 | 64 B | 1,087,339 | 217,443 | 259,128 | **5.00x** | **4.20x** |
| 10 | 256 B | 876,518 | 220,417 | 262,828 | **3.98x** | **3.33x** |
| 50 | 64 B | 1,358,583 | 249,111 | 244,432 | **5.45x** | **5.56x** |
| 50 | 256 B | 947,160 | 182,194 | 187,915 | **5.20x** | **5.04x** |
| 1000 | 64 B | 4,216,993 | 155,442 | 139,218 | **27.13x** | **30.29x** |
| 1000 | 256 B | 2,308,882 | 455,928 | 223,607 | **5.06x** | **10.33x** |

### Bulk throughput (MB/s — higher is better)

Single large payload per channel. NetConduit wins 9/18 comparisons against Go multiplexers. The credit-based flow control has more visible per-transfer cost at the largest payloads.

| Channels | Data | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 KB | 6.2 | 8.4 | 6.8 | 0.75x | 0.91x |
| 1 | 100 KB | 618.1 | 332.4 | 573.1 | **1.86x** | **1.08x** |
| 1 | 1 MB | 884.3 | 1,246.9 | 2,230.9 | 0.71x | 0.40x |
| 10 | 1 KB | 80.5 | 44.7 | 31.2 | **1.80x** | **2.58x** |
| 10 | 100 KB | 1,228.2 | 859.3 | 1,819.5 | **1.43x** | 0.68x |
| 10 | 1 MB | 2,127.8 | 1,762.1 | 3,256.3 | **1.21x** | 0.65x |
| 100 | 1 KB | 78.8 | 43.7 | 36.8 | **1.80x** | **2.14x** |
| 100 | 100 KB | 1,153.4 | 1,078.0 | 2,273.4 | **1.07x** | 0.51x |
| 100 | 1 MB | 1,831.4 | 1,891.3 | 3,771.3 | 0.97x | 0.49x |

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