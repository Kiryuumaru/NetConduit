# Plan 023: Direct Delivery on Read Path — Bypass Per-Channel Pipe

**Addresses:** Problem 3 (PROBLEMS.md) — Triple memory copy on receive path (385μs/MB)

**Change:** `src/NetConduit/ReadChannel.cs` — When user's `ReadAsync` is waiting for data and `EnqueueData` is called, copy the payload directly into the user's buffer instead of going through the per-channel Pipe. This eliminates 1 of 2 copies on the hot path (Pipe write in EnqueueData).

**Theory:** Current path: ReadLoop → EnqueueData copies to Pipe (copy #1) → ReadAsync wakes → ConsumeBuffer copies from Pipe to user buffer (copy #2). With direct delivery: ReadLoop → EnqueueData copies directly to user buffer (copy #1 only). For the benchmark (64KB frames, 64KB read buffer), every delivery fits perfectly. Saves ~200μs per 1MB of data on the receive side.

**Files modified:** `src/NetConduit/ReadChannel.cs`

**Mechanism:**
1. ReadAsync registers `_directDeliveryTcs` + `_directDeliveryBuffer` under `_disposeLock` when no data is available
2. EnqueueData checks for active direct delivery first (under same `_disposeLock`):
   - If active: copy directly to user buffer, handle remainder via Pipe if needed, SetResult
   - If not: use Pipe as before
3. SetClosed/Dispose cancel any pending TCS

**Success criteria:**
- All tests pass
- Bulk throughput ratios improve (NC vs FRP or NC vs Smux closer to 1.0x)
- Game-tick ratios do not regress
