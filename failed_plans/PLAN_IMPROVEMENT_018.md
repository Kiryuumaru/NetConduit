# Plan 018: Contention-Adaptive Flush (Skip ForceFlush When Draining)

**Addresses:** Multi-channel throughput regression from Plan 013 (per-frame ForceFlush contention on _streamLock)

## Analysis

Plan 013 added ForceFlush per frame >= 65536 bytes. This is optimal for single-channel (immediate pipelining) but causes _streamLock contention for multi-channel (100 channels × ForceFlush = serialized drains).

Plan 017 tried batching via accumulation threshold. This helped multi-channel but hurt single-channel by reducing pipelining.

**Key insight:** The optimal behavior differs by channel count:
- Single-channel: ForceFlush per frame → best pipelining
- Multi-channel: Skip ForceFlush when drain is busy → natural batching

We can detect multi-channel contention by checking `_streamLock.CurrentCount`:
- Count > 0: lock free → flush immediately (single-channel path)
- Count == 0: lock busy → skip flush (multi-channel path, current drainer handles all data)

## Change

**File:** `src/NetConduit/WriteChannel.cs` — WriteAsync threshold check

Replace:
```csharp
if (toSend >= 65536)
    await _multiplexer.ForceFlushPipeToStreamAsync(cancellationToken).ConfigureAwait(false);
```

With:
```csharp
if (toSend >= 65536 && _multiplexer.IsStreamLockAvailable)
    await _multiplexer.ForceFlushPipeToStreamAsync(cancellationToken).ConfigureAwait(false);
```

**File:** `src/NetConduit/StreamMultiplexer.cs` — Add property

```csharp
internal bool IsStreamLockAvailable => _streamLock.CurrentCount > 0;
```

## Expected Behavior

| Scenario | Plan 013 | Plan 018 |
|----------|----------|----------|
| 1ch×1MB | 16 ForceFlush | 16 ForceFlush (lock always free) |
| 10ch×1MB | 160+ ForceFlush (contention) | ~10-20 ForceFlush (skip when busy) |
| 100ch×1MB | 1600+ ForceFlush (heavy contention) | ~10-20 ForceFlush (heavy skip) |
| Game-tick 64B | FlushLoop timer | FlushLoop timer (no change) |

## Success Criteria

- Single-channel throughput ≥ Plan 013 baseline (no regression)
- Multi-channel throughput improves
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%
