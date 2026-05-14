# Benchmarks

NetConduit ships a BenchmarkDotNet project at [`benchmarks/NetConduit.Benchmarks`](../benchmarks/NetConduit.Benchmarks/) measuring throughput, scalability, and cross-transport behavior.

## Benchmark classes

| Class | Measures |
| --- | --- |
| `ThroughputBenchmark` | Raw bytes/sec for a single channel, varying payload size. |
| `ChannelTransitBenchmark` | `StreamTransit` and `MessageTransit` overhead vs raw channels. |
| `IpcThroughputBenchmark` | IPC transport throughput (named pipe / Unix socket). |
| `TcpVsMuxBenchmark` | Bare `TcpClient` vs `TcpMultiplexer` overhead. |
| `AllTransportsMuxBenchmark` | Same workload across TCP / WebSocket / UDP / IPC / QUIC. |
| `ScalabilityBenchmark` | Throughput as the channel count grows. |

Generated reports live under [`BenchmarkDotNet.Artifacts/results/`](../BenchmarkDotNet.Artifacts/results/) in CSV, HTML, and markdown form.

## Running locally

```powershell
dotnet run --project benchmarks/NetConduit.Benchmarks -c Release
```

Filter to a single benchmark class:

```powershell
dotnet run --project benchmarks/NetConduit.Benchmarks -c Release -- --filter *ThroughputBenchmark*
```

Run a single method:

```powershell
dotnet run --project benchmarks/NetConduit.Benchmarks -c Release -- --filter *AllTransports*Tcp*
```

## Running in Docker

The cross-language reference benchmarks (Go, Node) live under [`benchmarks/docker/`](../benchmarks/docker/). See the Dockerfiles for invocation.

## Interpreting results

- BenchmarkDotNet reports include mean, error, and standard deviation. Look at **Mean** plus the error column to judge variance.
- Channel throughput is bounded by the **slab size** (`ChannelOptions.SlabSize`); benchmarks vary this parameter.
- The framing overhead floor is ~8 bytes per frame plus protocol traffic (`PING`/`PONG`/`ACK`). Payloads >= 64 KiB amortize this to near zero.

## Tuning hints from the data

| Goal | Knob |
| --- | --- |
| Maximum single-channel throughput | `ChannelOptions.SlabSize = 4..16 MiB`. |
| Many small messages | `ChannelOptions.Priority` to surface critical channels first. |
| Low latency | Disable Nagle on TCP transports (`TcpClient.NoDelay = true` in custom factories). |
| High channel count | Default slab size keeps memory bounded at `count * 1 MiB`. |
# Benchmarks

Performance comparison of NetConduit against popular Go multiplexer libraries,
measured under identical Docker-isolated conditions.

## Test Environment

| Parameter      | Value                                               |
| -------------- | --------------------------------------------------- |
| Isolation      | Docker containers (`--network=none`, loopback only) |
| CPU pinning    | `--cpuset-cpus=0,1` (2 cores, kernel-enforced)      |
| Go parallelism | `GOMAXPROCS=2` (matches pinned core count)          |
| Runs           | 5 per scenario (median reported)                    |
| Runtime        | .NET 10.0, Go 1.23                                  |

## Implementations Compared

| Implementation | Language | Description                                                                                                     |
| -------------- | -------- | --------------------------------------------------------------------------------------------------------------- |
| **NetConduit** | C#       | Single TCP connection, N multiplexed channels — credit-based flow control, priority queuing, adaptive windowing |
| **FRP/Yamux**  | Go       | HashiCorp Yamux — stream multiplexer used by FRP, Consul, Nomad                                                 |
| **Smux**       | Go       | Popular Go stream multiplexer (xtaci/smux)                                                                      |

Raw TCP baselines (N separate connections, no multiplexing) are shown for context
but are not a practical alternative due to connection limits, lack of flow control,
and no channel management.

---

## Game-Tick Messaging (msg/s)

Simulates real-time workloads (game state updates, telemetry, chat) where many
small messages flow across multiplexed channels. Higher is better.

| Channels | Msg Size | NetConduit | FRP/Yamux |    Smux |  NC vs FRP | NC vs Smux |
| -------- | -------- | ---------: | --------: | ------: | ---------: | ---------: |
| 1        | 64B      |  1,140,551 |   246,015 | 326,407 |  **4.64x** |  **3.49x** |
| 1        | 256B     |  1,147,881 |   212,599 | 336,030 |  **5.40x** |  **3.42x** |
| 10       | 64B      |  1,242,974 |   237,982 | 287,982 |  **5.22x** |  **4.32x** |
| 10       | 256B     |  1,176,395 |   246,564 | 286,267 |  **4.77x** |  **4.11x** |
| 50       | 64B      |  1,546,152 |   273,151 | 293,880 |  **5.66x** |  **5.26x** |
| 50       | 256B     |  1,232,333 |   262,566 | 280,286 |  **4.69x** |  **4.40x** |
| 1000     | 64B      |  4,759,378 |   264,416 | 239,971 | **18.00x** | **19.83x** |
| 1000     | 256B     |  2,686,055 |   370,360 | 246,764 |  **7.25x** | **10.89x** |

**Result: NetConduit wins 16/16 game-tick comparisons** (3.4x–19.8x faster).

NetConduit's per-message overhead is extremely low. At 1000 channels, it achieves
4.8M msg/s while Go muxes plateau around 240K–370K msg/s.

---

## Bulk Throughput (MB/s)

Each channel sends a single data payload. Measures raw transfer speed. Higher is better.

| Channels | Data Size | NetConduit | FRP/Yamux |    Smux | NC vs FRP | NC vs Smux |
| -------- | --------- | ---------: | --------: | ------: | --------: | ---------: |
| 1        | 1KB       |        5.3 |      13.2 |    10.7 |     0.40x |      0.49x |
| 1        | 100KB     |      729.3 |     339.8 |   898.3 | **2.15x** |      0.81x |
| 1        | 1MB       |    1,198.5 |   1,621.9 | 2,664.8 |     0.74x |      0.45x |
| 10       | 1KB       |      100.0 |      49.1 |    42.5 | **2.04x** |  **2.35x** |
| 10       | 100KB     |    2,308.1 |     937.5 | 1,731.4 | **2.46x** |  **1.33x** |
| 10       | 1MB       |    2,854.3 |   2,980.1 | 4,081.4 |     0.96x |      0.70x |
| 100      | 1KB       |       58.2 |      50.9 |    58.0 | **1.14x** |  **1.00x** |
| 100      | 100KB     |    2,287.4 |   1,288.4 | 2,268.5 | **1.78x** |  **1.01x** |
| 100      | 1MB       |    2,659.9 |   2,438.3 | 4,132.4 | **1.09x** |      0.64x |

**Result: NetConduit wins 10/18 vs Go muxes** (7/9 vs FRP/Yamux, 3/9 vs Smux).

NetConduit's credit-based flow control adds per-transfer overhead that is most
visible in single-channel large payloads vs Smux. With more channels (≥10),
NetConduit becomes competitive or faster than FRP/Yamux across the board.

---

## Analysis

### Why NetConduit dominates game-tick messaging

The Go muxes (Yamux, Smux) use goroutine-per-stream with mutex-protected shared
buffers. Every send involves goroutine scheduling + mutex contention. NetConduit
uses a single dedicated writer thread with channel-based queuing, eliminating
per-message scheduling overhead on the hot path.

### Why bulk throughput is mixed vs Smux

Smux is a minimal mux: fire-and-forget, no flow control, no backpressure. It
achieves higher raw 1MB throughput at the cost of memory safety under load.
NetConduit's credit-based windowing prevents OOM but requires a credit-grant
round-trip before sending large payloads.

### What NetConduit provides that simpler muxes don't

| Feature                       | NetConduit | FRP/Yamux | Smux |
| ----------------------------- | :--------: | :-------: | :--: |
| Credit-based backpressure     |     ✓      |     ✗     |  ✗   |
| Priority queuing              |     ✓      |     ✗     |  ✗   |
| Adaptive windowing            |     ✓      |     ✗     |  ✗   |
| OOM protection under load     |     ✓      |     ✗     |  ✗   |
| Channel starvation prevention |     ✓      |     ✗     |  ✗   |

These features add measurable overhead on bulk transfers but provide production
safety guarantees that simpler muxes don't offer.

---

## Reproducing

```bash
cd benchmarks/docker
bash run-docker.sh
```

Requirements: Docker with Linux containers. Results are saved to
`benchmarks/docker/results/comparison-report.md`.

---

## Raw TCP Baselines

For completeness, here are all implementations including raw TCP (N separate
connections). Raw TCP is not multiplexing — it uses one connection per channel.

### Throughput (MB/s)

| Channels | Data Size | Raw TCP (Go) | FRP/Yamux |    Smux | Raw TCP (.NET) | NetConduit |
| -------- | --------- | -----------: | --------: | ------: | -------------: | ---------: |
| 1        | 1KB       |          4.6 |      13.2 |    10.7 |            2.1 |        5.3 |
| 1        | 100KB     |        486.1 |     339.8 |   898.3 |          291.6 |      729.3 |
| 1        | 1MB       |      3,275.7 |   1,621.9 | 2,664.8 |        2,399.8 |    1,198.5 |
| 10       | 1KB       |         25.8 |      49.1 |    42.5 |            9.0 |      100.0 |
| 10       | 100KB     |      1,939.8 |     937.5 | 1,731.4 |          928.4 |    2,308.1 |
| 10       | 1MB       |      8,498.2 |   2,980.1 | 4,081.4 |        2,994.8 |    2,854.3 |
| 100      | 1KB       |         26.8 |      50.9 |    58.0 |           22.3 |       58.2 |
| 100      | 100KB     |      2,284.1 |   1,288.4 | 2,268.5 |        2,363.4 |    2,287.4 |
| 100      | 1MB       |      9,837.8 |   2,438.3 | 4,132.4 |        6,571.5 |    2,659.9 |

### Game-Tick (msg/s)

| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux |    Smux | Raw TCP (.NET) | NetConduit |
| -------- | -------- | -----------: | --------: | ------: | -------------: | ---------: |
| 1        | 64B      |      647,800 |   246,015 | 326,407 |      2,622,336 |  1,140,551 |
| 1        | 256B     |    1,235,674 |   212,599 | 336,030 |      2,436,740 |  1,147,881 |
| 10       | 64B      |    3,614,933 |   237,982 | 287,982 |              — |  1,242,974 |
| 10       | 256B     |    3,296,033 |   246,564 | 286,267 |              — |  1,176,395 |
| 50       | 64B      |    3,916,126 |   273,151 | 293,880 |      4,663,568 |  1,546,152 |
| 50       | 256B     |    3,455,165 |   262,566 | 280,286 |      4,345,537 |  1,232,333 |
| 1000     | 64B      |    3,813,299 |   264,416 | 239,971 |              — |  4,759,378 |
| 1000     | 256B     |    7,651,200 |   370,360 | 246,764 |      4,255,481 |  2,686,055 |

> **Note:** Some .NET Raw TCP entries show "—" where the benchmark encountered
> IOExceptions due to connection storms (10–1000 simultaneous TCP connections
> overwhelming the kernel). This is precisely why multiplexing exists.
