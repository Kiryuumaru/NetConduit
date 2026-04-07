# Performance Hardening Plan — Phase 2

Targeted optimizations based on **measured profiling data** (not estimates).
All changes internal to the multiplexer — public API unchanged.

Profiling tool: `HotPathProfiler` (Stopwatch.GetTimestamp()-based, per-operation breakdown).
Environment: loopback TCP, `taskset 0x3` (2 cores), Release build.

## Current Position

All benchmarks: loopback TCP, `taskset 0x3` (2 cores), GOMAXPROCS=2, 5 runs median.

### Game-Tick (msg/s)

| Channels | Msg | Before | After | FRP/Yamux | Smux | vs FRP | vs Smux |
|----------|-----|-------:|------:|----------:|-----:|-------:|--------:|
| 1 | 64B | 109,248 | **1,452,611** | 90,106 | 118,082 | **16.12x** | **12.30x** |
| 1 | 256B | 161,876 | **716,591** | 90,492 | 119,611 | **7.92x** | **5.99x** |
| 10 | 64B | 146,025 | **1,042,778** | 105,488 | 134,086 | **9.88x** | **7.78x** |
| 10 | 256B | 105,910 | **609,001** | 107,986 | 133,707 | **5.64x** | **4.56x** |
| 50 | 64B | 130,327 | **1,060,717** | 113,838 | 131,083 | **9.32x** | **8.09x** |
| 50 | 256B | 112,230 | **666,728** | 111,042 | 125,492 | **6.00x** | **5.31x** |
| 1000 | 64B | 105,784 | **~1,076,000** | 111,339 | 108,636 | **~9.66x** | **~9.90x** |
| 1000 | 256B | 99,979 | **915,500** | 290,950 | 109,222 | **3.15x** | **8.38x** |

Win rate: **8/8 vs Yamux**, **8/8 vs Smux**

1000ch×64B measured via deep-profile (benchmark harness hits socket TIME_WAIT across 5 runs).

### Bulk Throughput (MB/s)

| Channels | Size | Before | After | FRP/Yamux | Smux | vs FRP | vs Smux |
|----------|------|-------:|------:|----------:|-----:|-------:|--------:|
| 1 | 1KB | 5.9 | **6.4** | 2.0 | 6.1 | **3.22x** | **1.05x** |
| 1 | 100KB | 281.0 | **114.9** | 306.0 | 377.6 | 0.38x | 0.30x |
| 1 | 1MB | 569.3 | **551.5** | 671.8 | 926.6 | 0.82x | 0.60x |
| 10 | 1KB | 22.3 | **4.0** | 14.4 | 13.7 | 0.28x | 0.29x |
| 10 | 100KB | 699.6 | **378.0** | 450.2 | 562.2 | 0.84x | 0.67x |
| 10 | 1MB | 752.7 | **740.9** | 866.9 | 1,089.5 | 0.85x | 0.68x |
| 100 | 1KB | 17.7 | **10.7** | 18.3 | 13.9 | 0.58x | 0.77x |
| 100 | 100KB | 430.3 | **224.9** | 654.7 | 979.8 | 0.34x | 0.23x |
| 100 | 1MB | 490.4 | **725.0** | 1,196.8 | 1,646.4 | 0.61x | 0.44x |

Win rate: **1/9 vs Yamux**, **1/9 vs Smux** (regression from 3/9, 3/9)

### Write Latency Profile

| Channels | Avg | P50 | P90 | P99 |
|----------|-----|-----|-----|-----|
| 1 | 13.3 us | 11 us | 18 us | 39 us |
| 10 | 163.9 us | 153 us | 228 us | 468 us |
| 50 | 520.9 us | 413 us | 877 us | 3,130 us |
| 1000 (256B) | 14,092 us | 12,057 us | 23,624 us | 41,637 us |

---

## Measured Profiling Data

Collected via `deep-profile` with `HotPathProfiler` instrumentation enabled.
Note: profiling adds Stopwatch overhead (~5-10%). Percentages within each section are relative.

### TryParseFrame (server read loop, per data frame)

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Frames parsed | 2,960,738 | 1,428,554 | 212,107 |
| Total time | 3,553ms | 2,428ms | 343ms |
| **Avg per frame** | **1.2us** | **1.7us** | **1.6us** |
| Header parse | 0.1us (8.1%) | 0.1us (7.2%) | 0.1us (6.5%) |
| Channel lookup | 0.1us (6.3%) | 0.1us (7.5%) | 0.0us (3.0%) |
| **ArrayPool.Rent** | **0.3us (21.8%)** | **0.5us (31.4%)** | **0.4us (27.8%)** |
| Payload copy | 0.1us (9.0%) | 0.2us (10.1%) | 0.2us (11.1%) |
| **EnqueueData** | **0.3us (26.8%)** | **0.4us (20.8%)** | **0.4us (22.8%)** |

### ConsumeBuffer (per application read)

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Avg per call | 0.7us | 1.0us | 0.9us |
| Data copy | 0.1us (10.1%) | 0.1us (8.5%) | 0.1us (16.0%) |
| **Dispose/Return** | **0.2us (33.7%)** | **0.3us (26.4%)** | **0.2us (27.0%)** |
| Credit grant | 0.1us (13.0%) | 0.2us (15.3%) | 0.1us (11.4%) |

### Combined ArrayPool Cost Per Frame

| Scenario | Rent | Return | **Total** |
|----------|------|--------|-----------|
| 1ch×64B | 0.3us | 0.2us | **0.5us** |
| 50ch×64B | 0.5us | 0.3us | **0.8us** |
| 1000ch×256B | 0.4us | 0.2us | **0.6us** |

ArrayPool.Rent+Return = **30-40% of total per-frame read path cost**. This is the #1 bottleneck.

### ReadAsync Path

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Fast path (buffered) | 97.0% | 100.0% | 99.5% |
| Slow path (await) | 3.0% | 0.0% | 0.5% |
| Linked CTS allocs | 87,168 | 138 | 1,017 |

Linked CTS allocation is **negligible** — fast path dominates in all scenarios.

### FlushLoop (per cycle)

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Cycles | 723 | 145 | 2,026 |
| Avg per cycle | 630.5us | 26,612us | 374.1us |
| HasPendingGrants | 13.6us (2.2%) | 27.6us (0.1%) | **58.6us (15.7%)** |
| WritePendGrants | 21.4us (3.4%) | 9.8us (0.0%) | 9.4us (2.5%) |
| CommitPipeWriter | 1.8us (0.3%) | 7.2us (0.0%) | 1.6us (0.4%) |
| DrainPipe | 471.6us (74.8%) | 26,588us (99.9%) | 322.4us (86.2%) |
| **Stream.WriteAsync** | **436.9us (69.3%)** | **26,473us (99.5%)** | **313.7us (83.8%)** |
| Stream.FlushAsync | 0.3us (0.0%) | 0.6us (0.0%) | 0.3us (0.1%) |

### Drain Characteristics

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Multi-segment | 78.4% | 4.8% | 0.4% |
| Avg batch size | 302 KB | 1.9 MB | 49 KB |
| Total drained | — | 263.2 MB | 94.2 MB |

### GC Pressure

| Metric | 1ch×64B | 50ch×64B | 1000ch×256B |
|--------|---------|----------|-------------|
| Gen0/sec | 22.3 | 33.0 | 49.8 |
| Gen1 | 48 | 61 | 89 |
| Gen2 | 13 | 8 | 9 |

### Throughput Asymmetry (sender vs receiver)

| Scenario | Sent (client) | Parsed (server) | Ratio |
|----------|---------------|-----------------|-------|
| 1ch×64B | — | 2,960,738 | — |
| 50ch×64B | 4,297,165 | 1,428,554 | 33% |
| 1000ch×256B | 2,588,285 | 212,107 | 8% |

At 1000ch, the server processes only 8% of sent messages in the measurement window.
Credit starvation on the sender (2.5-4M events) is the flow control working as designed,
gating the sender to the receiver's processing rate.

### Key Finding: The 1000ch×256B Yamux Gap (CLOSED)

Before: NetConduit 99,979 msg/s vs Yamux 290,950 msg/s (2.9x gap).
After: NetConduit **915,500 msg/s** vs Yamux 290,950 msg/s (**3.15x ahead**).

### Bulk Throughput Regression

Game-tick improved **7-13x** across all scenarios, but bulk throughput regressed in
mid-range scenarios (1KB, 100KB). The per-channel Pipe adds overhead for large bulk
transfers where the old OwnedMemory approach had less per-frame overhead on the write
side (Channel.TryWrite was cheaper than PipeWriter.GetSpan+Advance+FlushAsync).
The 1MB scenarios are comparable or improved due to better batching.

---

## 1. Header Parse Fast Path (FirstSpan) — ✅ DONE

**Measured cost:** Header parse = 0.1us per frame (6.5-8.1% of TryParseFrame)
**Savings:** ~0.05us per frame (eliminate stackalloc copy when contiguous)

When the PipeReader buffer is a single segment (common), read directly from `FirstSpan`.

```csharp
FrameHeader header;
if (buffer.FirstSpan.Length >= FrameHeader.Size)
    header = FrameHeader.Read(buffer.FirstSpan);
else
{
    Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
    buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
    header = FrameHeader.Read(headerBytes);
}
```

### Files

- `src/NetConduit/StreamMultiplexer.cs` — TryParseFrame

---

## 2. Remove EnqueueData Lock — ❌ REVERTED (data loss race)

**Measured cost:** Lock hold time = 0.1us per frame
**Context:** Total EnqueueData = 0.3-0.4us. Lock is ~25% of EnqueueData cost.

**Result:** TOCTOU race between volatile `_isDisposing` check and `TryWrite` causes data loss
during disposal. Test `Stress_HeavyLoad_DataIntegrity_ChecksumVerification` caught it (expected
50 channels, got 44). The lock is required for correctness — no safe alternative exists.

`EnqueueData` acquires `_disposeLock` on every incoming data frame. Removing it creates a
TOCTOU race: between the volatile check and `TryWrite`, `Dispose` can call `TryComplete()`,
causing `TryWrite` to fail silently and lose data. The lock is the only correct approach.

---

## 3. Multi-Segment Drain Without Copy — ✅ DONE

**Measured cost:** At 1ch, 78.4% of drains are multi-segment with avg batch 302KB
**At 50ch/1000ch:** <5% multi-segment (negligible)
**Impact:** Primarily helps 1ch bulk throughput

Currently rents an array and copies multi-segment pipe output into one contiguous buffer.

### Approach: Per-Segment WriteAsync

```csharp
if (buffer.IsSingleSegment)
{
    await writeStream.WriteAsync(buffer.First, ct).ConfigureAwait(false);
}
else
{
    foreach (var segment in buffer)
        await writeStream.WriteAsync(segment, ct).ConfigureAwait(false);
}
await writeStream.FlushAsync(ct).ConfigureAwait(false);
```

Eliminates the copy+allocation for multi-segment drains. Trade-off: more syscalls.
On loopback TCP, the copy is more expensive than extra syscalls for 302KB batches.

### Files

- `src/NetConduit/StreamMultiplexer.cs` — WriteBufferToStreamAsync

---

## 4. FlushLoop Channel Scan O(N) → O(pending) — ✅ DONE

**Measured cost:** HasPendingCreditGrants at 1000ch = 58.6us (15.7% of FlushLoop cycle)
**Total impact at 1000ch:** 2,026 cycles × 58.6us = 119ms in 3s (~4% of wall time)
**At 50ch:** 27.6us × 145 cycles = 4ms total (negligible)

`HasPendingCreditGrants()` and `WritePendingCreditGrants()` iterate ALL channels every cycle.
Cost scales linearly with channel count.

### Approach: Pending Credit Queue

```csharp
private readonly ConcurrentQueue<ReadChannel> _pendingCreditChannels = new();
```

1. `ConsumeBuffer` sets pending credits → `_pendingCreditChannels.Enqueue(this)` → `SignalFlush()`
2. FlushLoop: `while (_pendingCreditChannels.TryDequeue(out var ch))` → write credit frame

Eliminates both scan methods. Cost becomes O(channels-with-pending-credits) instead of O(all).

### Files

- `src/NetConduit/StreamMultiplexer.cs` — FlushLoopAsync
- `src/NetConduit/ReadChannel.cs` — ConsumeBuffer enqueues to pending set

---

## 5. Eliminate Per-Frame ArrayPool Rent/Return — ✅ DONE

**Measured cost:** 0.5-0.8us per frame (Rent 0.3-0.5us + Return 0.2-0.3us)
**% of read path:** 30-40% of TryParseFrame + ConsumeBuffer combined
**Scaling:** Rent cost rises from 0.3us (1ch) to 0.5us (50ch) — contention effect

Every data frame: `ArrayPool.Rent(payloadLength)` → copy from Pipe → enqueue → user reads →
`ArrayPool.Return()`. For 64B frames, the Rent+Return cycle is the single largest per-frame cost.

### Approach: Per-Channel Pipe

Replace `Channel<OwnedMemory>` with a per-channel `Pipe`. TryParseFrame copies the payload
directly into the channel's PipeWriter (variable-length write). ReadAsync reads from the
channel's PipeReader. No ArrayPool, no OwnedMemory wrapper, no heap allocation per frame.

```
Current: PipeReader → Rent → copy → OwnedMemory → Channel<T>.Write → ReadAsync → copy to user → Return
New:     PipeReader → copy → channel PipeWriter → ReadAsync reads from channel PipeReader
```

Eliminates: ArrayPool.Rent, ArrayPool.Return, OwnedMemory class allocation, OwnedMemory.Dispose.
Saves: 0.5-0.8us per frame + 1 heap allocation per frame (reduces GC pressure).

Complication: Credit tracking. Currently credits are granted when the user consumes a buffer
(ConsumeBuffer disposes OwnedMemory → triggers credit grant). With a Pipe, need a different
mechanism to track how many bytes the user has consumed and grant credits accordingly.

### Files

- `src/NetConduit/Internal/OwnedMemory.cs` — possibly removed
- `src/NetConduit/ReadChannel.cs` — Channel<OwnedMemory> → Pipe
- `src/NetConduit/StreamMultiplexer.cs` — TryParseFrame writes to channel Pipe

---

## Deprioritized / Dropped

### Eliminate Linked CTS in ReadAsync — DROPPED

**Measured:** Fast path = 97-100%. Linked CTS allocs: 138 out of 1.4M reads (50ch).
Impact is negligible. Not worth the code change.

### Reduce Stats Atomics — DROPPED

**Estimated:** ~56ns per round-trip (6 Interlocked ops). Less than 5% of per-frame cost.
Complexity of thread-local batching outweighs marginal gain.

---

## Measured Impact Projections

| # | Change | Measured savings/frame | Scenarios helped |
|---|--------|----------------------|------------------|
| 1 | Header parse FastSpan | ~0.05us | All (marginal) |
| 2 | Remove EnqueueData lock | 0.1us | All (modest) |
| 3 | Multi-segment drain | Copy avoidance (302KB) | 1ch bulk |
| 4 | FlushLoop O(pending) | 58.6us/cycle at 1000ch | High channel count |
| 5 | Eliminate ArrayPool | 0.5-0.8us | All (30-40% of read path) |

---

## Structural Gaps (Not Addressable Without Feature Removal)

These gaps exist because NetConduit provides capabilities that Yamux/Smux do not:

| Feature | Measured/Estimated Cost | Go Muxes |
|---------|------------------------|----------|
| Credit-based flow control | ~50-100ns (CAS + grant frames) | None (unbounded) |
| Adaptive windowing | ~50-80ns (RecordConsumption) | Fixed or none |
| Priority queuing | ~10-20ns (priority check) | FIFO only |
| Reconnection buffer | ~5-500ns (RecordSend) | No reconnection |
| Per-channel stats | ~56ns (6 Interlocked ops) | No stats |

The credit round-trip latency (receive → consume → grant → flush → TCP → parse → release)
is the architectural throughput limiter at high channel counts. Senders stall waiting for
credits — 2.5-4M starvation events across all scenarios.

---

## Test Plan

381 tests across 6 projects. Every step: build (0 warnings) → test (100% pass) → benchmark.

### Test Failure Response

- MUST fully revert any step that causes test failures
- NEVER apply next step on top of failing step
- NEVER comment out or skip tests
