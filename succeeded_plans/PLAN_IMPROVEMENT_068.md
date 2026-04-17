# Plan 068: Fix benchmark reconnection deadlock + larger Pipe segments (65KB)

## Problem

Every plan since 061 has failed due to NC 1000ch×64B TimeoutException. Root cause analysis reveals this is a **benchmark reliability bug**, not a mux performance issue:

1. Raw TCP 1000ch test runs immediately before Mux 1000ch, stressing the TCP stack
2. If the TCP connection breaks during Mux channel setup, the **client auto-reconnects** (default `EnableReconnection = true`) to the same TcpListener
3. The **server mux is dead** (`EnableReconnection = false`), so nobody reads the handshake
4. Client's `HandshakeTimeout = Infinite` → hangs forever waiting for handshake response
5. `OpenChannelAsync`'s 30s per-channel timeout fires → TimeoutException
6. The 30s deadlock wastes time instead of failing fast, and prevents any retry

Evidence: Plan 065 proved the mux CAN handle 1000ch (1,146,571 msg/s, 13.37x ratio in Run 1). The timeout only occurs under system degradation when Raw TCP also fails.

## Changes

### Change 1: Fix benchmark client reconnection (benchmark bug fix)

**File:** `benchmarks/docker/netconduit-comparison/Program.cs`

In `RunGameTickMuxAsync` and `RunThroughputMuxAsync`, set `EnableReconnection = false` on the client multiplexer:

```csharp
var clientOptions = TcpMultiplexer.CreateOptions("127.0.0.1", port, opts => opts.EnableReconnection = false);
```

**Why:** The benchmark creates fresh connections per run. Reconnection is useless in a benchmark and causes a fatal deadlock when the TCP connection fails (client reconnects to orphaned TcpListener, hangs for 30s). With reconnection disabled, connection failures propagate immediately.

### Change 2: Add per-run retry in game-tick benchmark

**File:** `benchmarks/docker/netconduit-comparison/Program.cs`

In `RunGameTickMuxAsync`, wrap each run in retry logic (max 2 retries per run). If channel setup fails due to transient TCP issues, dispose everything and retry with a fresh connection. This handles environmental instability from preceding Raw TCP tests.

**Why:** The 5-run benchmark has no tolerance for transient TCP failures. A single failed run crashes the entire test. With retry, transient failures (port exhaustion, GC pressure, TCP stack stress) are handled gracefully — consistent mux bugs would still fail all retries.

### Change 3: Larger Pipe segments (65KB)

**File:** `src/NetConduit/StreamMultiplexer.cs`

Set `minimumSegmentSize: 65536` on the Pipe:

```csharp
_pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, minimumSegmentSize: 65536));
```

**Why:** Default 4KB segments cause excessive segmentation:
- Throughput (64KB frames): each frame spans 16+ segments → 16 WriteAsync calls in drain. With 65KB segments: 1 segment per frame → 1 WriteAsync call
- Game-tick (1000ch, 73B frames): 1ms FlushLoop accumulates ~1000 frames across ~18 segments. With 65KB: ~2 segments → faster drain
- Net effect: fewer segment allocations, shorter ReadOnlySequence chains, fewer drain syscalls

## Success Criteria

- Bulk throughput ratios improved (multi-channel scenarios closer to 1.0x)
- Game-tick ratios not regressed (all ratios ≥ current docs/benchmarks.md values)
- No failures (TimeoutException, IOException, etc.)
- All 3 benchmark runs pass
