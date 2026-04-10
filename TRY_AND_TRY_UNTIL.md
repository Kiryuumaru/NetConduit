# Try and Try Until

## Goal

Improve bulk throughput to 2x current (or near competitor speeds) while retaining game-tick performance. All tests must pass.

---

## Flow

### 1. Establish Baseline

Run full benchmark suite including all competitors:

```bash
bash benchmarks/docker/run-benchmarks.sh
```

Record the **NC vs FRP** and **NC vs Smux** ratios from the report as the baseline. Absolute numbers vary 15-25% between runs; ratios within the same run are stable because all implementations share identical conditions.

### 2. Create Plan

Create `PLAN_IMPROVEMENT_NNN.md` with a single focused change:
- What the change is
- Which files are modified
- Why it should help
- Success criteria (bulk throughput target, game-tick must not regress)

### 3. Execute Plan

1. Implement the change
2. `dotnet build` — 0 warnings, 0 errors
3. `dotnet test` — 100% pass
4. `bash benchmarks/docker/run-benchmarks.sh` — full suite with all competitors
5. Compare **ratios** (NC vs FRP, NC vs Smux) against baseline ratios

### 4. Evaluate

Compare the **NC vs FRP** and **NC vs Smux** ratio columns from the new run against the baseline run. Absolute MB/s or msg/s numbers are not comparable across runs.

**PASS** if:
- All tests pass
- Bulk throughput ratios improved (NC vs FRP or NC vs Smux closer to 1.0x or above)
- Game-tick ratios did not regress (NC vs FRP and NC vs Smux stay at or above baseline ratios)
- Game-tick 1ch×64B NC vs FRP ratio stays above 10x
- Game-tick 50ch×64B NC vs FRP ratio does not drop more than 5% from baseline

**FAIL** if any condition is not met.

### 5. On Fail

1. Revert all code changes: `git checkout -- src/`
2. Move `PLAN_IMPROVEMENT_NNN.md` to `failed_plans/PLAN_IMPROVEMENT_NNN.md`
3. Append entry to `failed_plans/INDEX.md`
4. Go back to step 2 with a new plan

### 6. On Pass

1. Move `PLAN_IMPROVEMENT_NNN.md` to `succeeded_plans/PLAN_IMPROVEMENT_NNN.md`
2. Come back to user with the results.

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
