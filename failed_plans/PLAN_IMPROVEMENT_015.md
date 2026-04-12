# Plan 015: Immediate Credit Grant Flush on Reader Side

**Addresses:** Credit grant round-trip latency (PROBLEMS.md Problem 1: 81% of wall time is credit starvation for sustained throughput)

## Analysis

Current path for credit grants:
1. Reader calls ConsumeBuffer → RecordConsumptionAndGetGrant triggers grant
2. Grant enqueued via EnqueuePendingCredit + SignalFlush
3. Receiver's FlushLoop wakes (~10-100µs scheduling delay) → writes grant frame → TCP
4. Sender parses grant → credits restored

The SignalFlush wakes FlushLoop, but on 2 cores with 6+ async tasks, scheduling delay adds ~10-100µs per credit round-trip. For sustained throughput with many credit cycles, this compounds.

## Change

**File:** `src/NetConduit/ReadChannel.cs`

In ConsumeBuffer, when a credit grant is triggered in batched mode:
1. Write the grant frame directly via `_multiplexer.SendCreditGrant()` (sync, writes to Pipe under _writeLock)
2. Set a flag `_needsCreditFlush = true`

In ReadAsync, after ConsumeBuffer returns, if `_needsCreditFlush`:
1. Call `await _multiplexer.ForceFlushPipeToStreamAsync()` to immediately send the grant to TCP
2. Reset flag

### Why this helps
- Eliminates FlushLoop scheduling delay for credit grants (~10-100µs per grant)
- Credits return faster → sender stalls less → higher sustained throughput
- Grant frequency is low: once per ~1MB consumed (25% of 4MB window). For game-tick 64B: 1 flush per ~16K reads. Overhead amortizes.

### Why previous credit plans failed
- Plans 005/008/009: Changed WHERE grants trigger, not HOW they're flushed
- Plan 007: Direct-to-stream (bypassed Pipe entirely) caused lock contention
- This plan: grants go through Pipe normally, but commit+drain immediately (same pattern as Plan 013 for data)

## Success Criteria

- Bulk throughput improves (especially sustained scenarios like 10ch+ where credit cycles are frequent)
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%
