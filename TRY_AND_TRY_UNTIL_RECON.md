# Try and Try Until — Reconnection Without Degradation

## Goal

Remove the `EnableReconnection` option entirely. Reconnection support MUST be always-on — not optional. The reconnection machinery (ring buffer recording, sync state) must operate with zero or near-zero overhead so there is no reason to disable it. All tests must pass.

After removing the option, benchmark ratios (NC vs FRP, NC vs Smux) MUST retain at least 95% of current `docs/benchmarks.md` baselines.

### Root Cause

`EnableReconnection = true` activates `ChannelSyncState.StartRecording()` on every channel open. Once recording is active, every `WriteAsync` → `RecordSend()` call:

1. Acquires `lock(_lock)` — serializes all writes per channel
2. Copies data into a 1MB ring buffer — extra memcpy per frame
3. Updates ring buffer bookkeeping — wraparound position, acked offsets

When `_recording = false`, `RecordSend()` is just `Interlocked.Add` — lock-free, zero-copy.

### Degradation Observed (EnableReconnection=true vs false)

| Scenario | Degradation |
|----------|-------------|
| Bulk 1ch×1KB | -77% |
| Bulk 1ch×100KB | -71% |
| Bulk 1ch×1MB | -31% |
| Bulk 10ch×100KB | -57% |
| Bulk 100ch×100KB | -60% |
| Bulk 100ch×1MB | -40% |
| Game-tick | -3% to -7% (acceptable) |

---

## Flow

### 1. Create Plan

#### Research

Before proposing a change, gather evidence:

1. **Profile the hot path** — Read the current write/flush/drain code in `src/NetConduit/`. Trace the exact sequence from `WriteAsync` to socket write. Focus on `ChannelSyncState.RecordSend()` and its callers
2. **Search the codebase** — Use search tools to find all callers of `RecordSend`, `StartRecording`, `GetUnacknowledgedDataFrom`, `Acknowledge`, `EnableReconnection`, and reconnection-related code
3. **Search the internet** — Look for zero-copy ring buffer techniques, lock-free circular buffers, `System.IO.Pipelines` for replay buffering, deferred recording patterns, copy-on-disconnect strategies
4. **Review failed plans** — Read `failed_plans/INDEX.md` to avoid repeating approaches that already failed
5. **Review succeeded plans** — Read `succeeded_plans/INDEX.md` to understand what has already been applied

#### Write Plan

Create `PLAN_IMPROVEMENT_NNN.md` with:
- What the change is
- Which files are modified
- Analysis showing WHY it should help (with evidence from profiling/research, not speculation)
- Links or references to techniques/implementations that inspired the change
- Success criteria (ratios vs alternatives must retain ≥95% of current baselines after removing `EnableReconnection`)

### 2. Execute Plan

1. Implement the change
2. `dotnet build` — 0 warnings, 0 errors
3. `dotnet test` — 100% pass

### 3. Evaluate (3 runs)

Run the full benchmark suite **3 times**. The benchmark harness no longer sets `EnableReconnection` (the option no longer exists — reconnection is always on).

```bash
bash benchmarks/docker/run-benchmarks.sh
```

A run **PASSES** if every NC vs FRP and NC vs Smux ratio retains ≥95% of the corresponding ratio in `docs/benchmarks.md`.

Current baseline ratios from `docs/benchmarks.md`:

| Benchmark | NC vs FRP | NC vs Smux |
|-----------|-----------|------------|
| Bulk 1ch×1KB | 0.43x | 0.52x |
| Bulk 1ch×100KB | 0.77x | 0.48x |
| Bulk 1ch×1MB | 0.57x | 0.59x |
| Bulk 10ch×1KB | 0.81x | 0.83x |
| Bulk 10ch×100KB | 1.13x | 0.87x |
| Bulk 10ch×1MB | 1.00x | 0.60x |
| Bulk 100ch×1KB | 0.59x | 0.72x |
| Bulk 100ch×100KB | 1.01x | 0.65x |
| Bulk 100ch×1MB | 1.21x | 0.90x |
| GT 1ch×64B | 20.62x | 14.79x |
| GT 1ch×256B | 16.53x | 11.45x |
| GT 10ch×64B | 12.09x | 10.63x |
| GT 10ch×256B | 7.90x | 7.33x |
| GT 50ch×64B | 12.13x | 12.12x |
| GT 50ch×256B | 8.40x | 8.34x |
| GT 1000ch×64B | 11.35x | 17.33x |
| GT 1000ch×256B | 8.06x | 11.75x |

95% thresholds: multiply each ratio by 0.95. If any ratio in the run falls below its threshold, the run **FAILS**.

**PASS** if all 3 runs pass AND all tests pass.

**FAIL** if any single run fails.

### 4. On Fail

1. Revert all code changes: `git checkout -- src/`
2. Move `PLAN_IMPROVEMENT_NNN.md` to `failed_plans/PLAN_IMPROVEMENT_NNN.md`
3. Append entry to `failed_plans/INDEX.md`
4. Go back to step 1 with a new plan

### 5. On Pass

1. Move `PLAN_IMPROVEMENT_NNN.md` to `succeeded_plans/PLAN_IMPROVEMENT_NNN.md`
2. Update `docs/benchmarks.md` with the median of the 3 runs
3. Come back to user with the results.

---

## File Structure

```
PLAN_IMPROVEMENT_NNN.md          # Active plan (moved after pass/fail)
failed_plans/
  INDEX.md                       # Table of all failed plans
  PLAN_IMPROVEMENT_001.md        # First failed plan
  ...
succeeded_plans/
  PLAN_IMPROVEMENT_013.md        # First succeeded plan
  ...
```

---

## Commands Reference

| Step | Command |
|------|---------|
| Build | `dotnet build` |
| Test | `dotnet test` |
| Full benchmark | `bash benchmarks/docker/run-benchmarks.sh` |
| Revert src | `git checkout -- src/` |
