# Plan 062: Drain-until-empty loop + lower TryCommitAndDrain threshold

## What

Two changes to the write/drain path:

1. **Drain-until-empty loop** in `DrainPipeToStreamAsync` and `TryCommitAndDrainAsync`: Convert the single `TryRead` drain to a `while` loop that keeps reading while committed data is available. This picks up data committed by concurrent writers during drain, batching more data per TCP write cycle.

2. **Lower TryCommitAndDrain threshold** from 8192 to 1024 in `WriteChannel.cs`: Enables immediate drain for small-data throughput scenarios (1KB transfers) that currently wait up to 1ms for FlushLoop.

## Files Modified

- `src/NetConduit/StreamMultiplexer.cs` — `DrainPipeToStreamAsync` and `TryCommitAndDrainAsync`
- `src/NetConduit/WriteChannel.cs` — threshold constant

## Analysis

### Drain-until-empty loop (evidence-based)

Currently, `DrainPipeToStreamAsync` does a single `TryRead` → write → `AdvanceTo`. If concurrent writers commit data between `TryRead` and `_streamLock.Release()`, that data waits for the next drain cycle (up to 1ms for FlushLoop).

With the loop: after writing, `TryRead` again. If concurrent commits produced new data, drain it too. Loop exits when `TryRead` returns false (no more committed data). Self-terminating.

Multi-channel benefit: for 100ch × 100KB, writers commit frames concurrently. Currently, one drain cycle writes data from whatever was committed before `TryRead`. With the loop, data committed during the TCP `WriteAsync` gets picked up immediately.

Game-tick safety: small messages (< 1024 bytes) never trigger `TryCommitAndDrain`, so they only go through FlushLoop. FlushLoop's Phase 1 commits under `_writeLock` (no new commits possible during commit). Phase 2 calls `DrainPipeToStreamAsync`. First `TryRead` gets all committed data. Loop's second `TryRead` returns false (no new commits since `_writeLock` was just released and writers haven't committed yet). Loop exits after 1 iteration. Identical behavior.

### Lower threshold (evidence-based)

Current threshold: `toSend >= 8192` for TryCommitAndDrain.

For 1KB throughput: `toSend = 1024 < 8192` → no TryCommitAndDrain → waits up to 1ms for FlushLoop. At 1 transfer per ms: theoretical max ~1 MB/s. Current result: 1.4 MB/s (close to theoretical).

With threshold 1024: `toSend = 1024 >= 1024` → TryCommitAndDrain fires → immediate drain → throughput limited by TCP write latency, not FlushLoop timer.

Game-tick safety: game-tick messages are 64B and 256B, both < 1024. Threshold change has zero impact on game-tick.

### Combined effect

The drain loop helps multi-channel scenarios. The threshold helps small-data scenarios. Together they cover the two weakest throughput areas (100ch×1KB at 0.19x, 1ch×1KB at 0.17x).

## Success Criteria

- Bulk throughput ratios improved vs docs/benchmarks.md
- Game-tick ratios not regressed
- Game-tick 1ch×64B NC/FRP stays above 10x
- Game-tick 50ch×64B NC/FRP within 5% of docs (10.68x)
- No 1000ch×64B timeout
