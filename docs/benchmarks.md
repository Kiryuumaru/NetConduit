# Benchmarks

NetConduit ships a comparison benchmark suite that pits NetConduit's TCP multiplexer against raw TCP (.NET) and an equivalent Go reference implementation.

## What is benchmarked

The project at [`benchmarks/docker/netconduit-comparison`](../benchmarks/docker/netconduit-comparison/) runs two workloads:

| Workload | Measures |
| --- | --- |
| `throughput` | Sustained MB/sec moving data across 1 / 10 / 100 channels at 1 KiB / 100 KiB / 1 MiB payloads. |
| `game-tick` | Messages/sec under a tight tick loop across 1 / 10 / 50 / 1000 channels at 64 / 256 byte payloads. |

Each measurement is the median of 5 runs.

Two implementations are exercised per workload:

- **Raw TCP (.NET)** — bare `TcpClient` / `TcpListener`, no multiplexing.
- **NetConduit Mux TCP** — `StreamMultiplexer` over `NetConduit.Transport.Tcp`.

The Go side (`benchmarks/docker/go-comparison`) provides an apples-to-apples reference.

## Running

The full suite runs in a controlled Docker environment with CPU affinity and `GOMAXPROCS` pinned:

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

## Reports

`run-benchmarks.sh` writes JSON output to `benchmarks/docker/results/` and invokes `report.py` to produce the comparison table.

Legacy BenchmarkDotNet artifacts from older runs are kept under [`BenchmarkDotNet.Artifacts/results/`](../BenchmarkDotNet.Artifacts/results/) for reference.

## Interpreting results

- Numbers are reported as **MB/s** (throughput) and **msg/s** (game-tick).
- The multiplexer's overhead vs raw TCP is the cost of framing, flow control, and channel scheduling — it is bounded and amortizes to near zero on payloads >= 64 KiB.
- Channel scalability shows up most clearly in the `game-tick` 1000-channel rows.

## Tuning hints

| Goal | Knob |
| --- | --- |
| Maximum single-channel throughput | `ChannelOptions.SlabSize = 4..16 MiB` |
| Critical small messages | Raise `ChannelOptions.Priority` |
| Low latency | `TcpClient.NoDelay = true` in a custom `StreamFactory` |
| High channel count | Default 1 MiB slab keeps memory bounded at `count * 1 MiB` |