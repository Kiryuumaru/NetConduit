# Try and Try Until

## Goal

Improve bulk throughput to 2x current (or near competitor speeds) while retaining game-tick performance. All tests must pass.

---

## Flow

### 1. Create Plan

#### Research

Before proposing a change, gather evidence:

1. **Profile the hot path** — Read the current write/flush/drain code in `src/NetConduit/`. Trace the exact sequence from `WriteAsync` to socket write. Identify where time is spent (locks, allocations, syscalls, waiting)
2. **Search the codebase** — Use search tools to find all callers, all related constants/thresholds, and any existing comments explaining design choices
3. **Search the internet** — Look for techniques used by high-performance multiplexers (yamux, smux, quic-go, h2, kestrel). Search for: buffer pooling strategies, write coalescing, Nagle-like batching, zero-copy patterns, `System.IO.Pipelines` usage, `ValueTask` patterns, lock-free queues
4. **Review failed plans** — Read `failed_plans/INDEX.md` to avoid repeating approaches that already failed
5. **Review succeeded plans** — Read `succeeded_plans/INDEX.md` to understand what has already been applied

#### Write Plan

Create `PLAN_IMPROVEMENT_NNN.md` with:
- What the change is
- Which files are modified
- Analysis showing WHY it should help (with evidence from profiling/research, not speculation)
- Links or references to techniques/implementations that inspired the change
- Success criteria (bulk throughput target, game-tick must not regress)

### 2. Execute Plan

1. Implement the change
2. `dotnet build` — 0 warnings, 0 errors
3. `dotnet test` — 100% pass

### 3. Evaluate (3 runs)

Run the full benchmark suite **3 times**. Each run produces NC vs FRP and NC vs Smux ratios.

```bash
bash benchmarks/docker/run-benchmarks.sh
```

A run **PASSES** if:
- Bulk throughput ratios improved (NC vs FRP or NC vs Smux closer to 1.0x or above compared to current `docs/benchmarks.md`)
- Game-tick ratios did not regress (NC vs FRP and NC vs Smux stay at or above current `docs/benchmarks.md`)
- Game-tick 1ch×64B NC vs FRP ratio stays above 10x
- Game-tick 50ch×64B NC vs FRP ratio does not drop more than 5% from current `docs/benchmarks.md`

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
  PLAN_IMPROVEMENT_002.md        # Second failed plan
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
| Revert | `git checkout -- src/` |
