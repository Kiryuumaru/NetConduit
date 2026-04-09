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

Record results as the baseline. Every iteration re-runs this same command — no frozen numbers.

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
5. Compare against baseline

### 4. Evaluate

**PASS** if:
- All tests pass
- Bulk throughput improved toward 2x baseline (or near Yamux/Smux)
- Game-tick 1ch×64B stays above 1,200,000 msg/s
- Game-tick 50ch×64B does not regress more than 5%

**FAIL** if any condition is not met.

### 5. On Fail

1. Revert all code changes: `git checkout -- src/`
2. Move `PLAN_IMPROVEMENT_NNN.md` to `failed_plans/PLAN_IMPROVEMENT_NNN.md`
3. Append entry to `failed_plans/INDEX.md`
4. Go back to step 2 with a new plan

### 6. On Pass

Come back to user with the results.

---

## File Structure

```
PLAN_IMPROVEMENT_NNN.md          # Active plan (deleted after pass/fail)
failed_plans/
  INDEX.md                       # Table of all failed plans
  PLAN_IMPROVEMENT_001.md        # First failed plan
  PLAN_IMPROVEMENT_002.md        # Second failed plan
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
