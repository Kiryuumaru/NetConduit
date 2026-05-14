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
