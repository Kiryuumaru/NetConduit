# Plan 016: Increase Read PipeReader Buffer Size

**Addresses:** Read-side syscall overhead from undersized buffer (16KB for 64KB+ frames)

## Analysis

Current read buffer: 16,384 bytes (16KB). For 64KB frames, PipeReader performs ~4+ network reads per frame. Each syscall adds latency. On the read side, frames must be fully received before TryParseFrame can process them.

Increasing to 1MB allows fewer, larger reads from the transport stream, reducing per-frame overhead.

Plan 011 included this change (labeled "Change #1") but combined it with two other changes (adaptive flush signal + write coalescing) that caused regressions. This plan isolates the buffer increase.

## Change

**File:** `src/NetConduit/StreamMultiplexer.cs` — Line 442

Change `bufferSize: 16384` to `bufferSize: 1_048_576`.

## Success Criteria

- Bulk throughput improves (especially large-frame scenarios like 1ch×1MB, 100ch×1MB)
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%
