# Plan 011: Larger Read Buffer + Adaptive Flush Signal

**Addresses:** Bulk throughput bottleneck from undersized read buffer and batched-mode flush latency

## Changes

### 1. Increase Read PipeReader Buffer (16KB → 1MB)

**File:** `src/NetConduit/StreamMultiplexer.cs`

The read-side PipeReader wraps the transport stream with `bufferSize: 16384`. For bulk throughput frames (100KB–1MB), the PipeReader must perform 7–64 `ReadAsync` syscalls per frame to accumulate enough data. Each syscall costs ~300ns + context switch overhead.

Increasing to 1MB (1_048_576) allows 100KB frames to arrive in 1 read and 1MB frames in 1–2 reads.

Impact on game-tick: neutral — small frames are already batched regardless of buffer size. The buffer is only allocated when needed by the PipeReader internals.

### 2. Signal FlushLoop for Large Data Frames

**File:** `src/NetConduit/StreamMultiplexer.cs`

In `SendFrameToWriter`, always signal the FlushLoop when the data frame payload is >= 4KB. Currently, in Batched mode, data frames never signal and rely on the 1ms timer. For 1ch×100KB, this adds up to 1ms latency per frame.

Small frames (game-tick 64B–256B) continue to rely on the 1ms timer for batching efficiency.

### 3. Coalesce Multi-Segment Drain Writes

**File:** `src/NetConduit/StreamMultiplexer.cs`

In `WriteBufferToStreamAsync`, when the Pipe buffer has multiple segments (common with concurrent writers), coalesce them into a single contiguous write instead of iterating segments. Reduces N syscalls to 1.

## Why It Should Help

- Change 1 reduces read-side syscalls per bulk frame from 7–64 to 1–2
- Change 2 eliminates 0–1ms flush latency for bulk writes
- Change 3 reduces write-side syscalls for multi-channel scenarios

## Success Criteria

- Bulk throughput improves toward 2x baseline (or near Yamux/Smux)
- Game-tick 1ch×64B stays above 1,200,000 msg/s (baseline: 1,395,100)
- Game-tick 50ch×64B does not regress more than 5% (baseline: 1,138,289)
