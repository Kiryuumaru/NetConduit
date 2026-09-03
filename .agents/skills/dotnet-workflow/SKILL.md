---
name: dotnet-workflow
description: Use when building, testing, running samples, managing Test Category batches, or doing pre-commit verification for this NUKE plus dotnet repo.
---

# Dotnet Workflow

Build, test, and verify NetConduit the same way every time. NEVER invent other build, publish, or run commands.

## 1. Commands

### NUKE (repo entry points)

`build.ps1` (Windows) and `build.sh` (Linux/macOS) bootstrap the NUKE build in `build/_build.csproj`. Only these NUKE targets are carried over from prior instructions:

| Command | Purpose |
|---|---|
| `./build.sh init` (or `.\build.ps1 init`) | First-time setup, if provided by the build bootstrap |
| `./build.sh clean` (or `.\build.ps1 clean`) | Clean build artifacts (`bin`, `obj`, `.vs`) |

First-time setup is init (if provided), then build:

1. `./build.sh init` (if the bootstrap provides it)
2. `dotnet build`

### .NET (daily use)

| Command | Purpose |
|---|---|
| `dotnet build` | Build the solution. MUST end 0 warnings, 0 errors. |
| `dotnet test` | Run ALL tests. MUST end 100% pass. |
| `dotnet test tests/NetConduit.UnitTests` | Run core unit tests only. |
| `dotnet test tests/NetConduit.Transit.<Name>.UnitTests` | Run one transit suite (`Stream`, `DuplexStream`, `Message`, `DeltaMessage`). |
| `dotnet test tests/NetConduit.Transport.<Name>.IntegrationTests` | Run one transport suite (`Tcp`, `WebSocket`, `Udp`, `Ipc`, `Quic`). |

- MUST use `dotnet build` for builds and `dotnet test` for tests. No other build/test commands exist in this repo.
- MUST treat publishing as out of scope for this skill — no publish flow exists in this repo since the old publish steps referenced stale paths that do not exist
- MUST scope `dotnet test <project>` to a single suite only for fast iteration. The gate is always the FULL `dotnet test`.
- NEVER reference `tests/Domain.UnitTests`, `tests/Application.UnitTests`, `src/Presentation.*`, `sampleapp.exe`, `--urls` flags, or `src/Domain/AppEnvironment/Constants/AppEnvironments.cs` — none of those paths exist in NetConduit. They are stale leftovers from a different project template.

### Samples

Runnable samples live in `samples/` (`FileTransferSample`, `GroupChatSample`, `PongGame`, `RemoteShellSample`, `RpcFrameworkSample`, `ScoreboardSample`, `SimpleTcpTunnel`).

- MUST run a sample with `dotnet run --project samples/<SampleName>` using the exact directory name.
- MUST read the sample's `README.md` first for any required setup.
- NEVER invent a `src/Presentation.*` run target.

## 2. BUILD Phase

From the remote dev playbook, adapted to this repo:

- MUST run `dotnet build` after every unit of work. NEVER present code that has not been built.
- MUST require 0 errors and 0 warnings. Every warning is a potential bug — fix the root cause, NEVER suppress warnings instead.
- MUST check the project config with tools (target frameworks, language version, dependencies) rather than recalling from memory.
- MUST NOT proceed to TEST when BUILD is red. BUILD red means back to IMPLEMENT.

## 3. TEST Phase

- MUST run the full `dotnet test` before presenting. 100% pass is required — PASSED means actually green in the runner output, NEVER inferred from absence of error.
- MUST cover every change with tests: happy path, edge cases (empty inputs, nulls, boundary values), error cases (invalid inputs, failure scenarios), and integration with neighboring components where applicable.
- MUST apply NetConduit-relevant real-world scenarios with judgment, not blindly: connection drops mid-operation, reconnect/resume and write replay, keepalive timeout, backpressure under load, concurrent channel use, malformed frames from a confused peer.
- MUST keep tests deterministic, isolated, fast (unit tests in milliseconds), readable, and automated.
- MUST NOT ask anyone else to test, run, or verify — running build and tests is this skill's job.
- MUST NOT say "this should work" — MUST know it works from runner evidence.

## 4. Test Categorization (xUnit Batches)

`HighMemory` tests live in `tests/NetConduit.UnitTests` (`TestCategories.cs`, `MemoryLeakTests.cs`, `HighMemoryCollection.cs`). They need isolated CI batching:

- MUST use xUnit `Trait("Category", "Name")` for tests that require isolated or grouped CI execution.
- MUST leave normal fast tests uncategorized.
- MUST run uncategorized tests first with category exclusion filters.
- MUST run categorized tests after uncategorized tests.
- MUST run tests with the same category in the same batch.
- MUST assign a unique category to any test that must run alone.
- MUST discover category names dynamically from compiled test assemblies.
- MUST order categorized batches by discovered test count descending, then category name.
- NEVER add class-level `Category` traits when individual tests in the class need different batch isolation.
- NEVER hardcode category lists in build orchestration.

| Category | Purpose |
|---|---|
| `HighMemory` | Long-running or high-memory tests that can run together |
| `HighMemory.HappyPath` | Isolated memory-leak happy-path test |

## 5. Debug Discipline

When something fails, MUST investigate properly:

1. **Gather** — Capture actual output: logs, stack traces, error messages, system state.
2. **Reproduce** — Create a minimal reproduction; for code bugs write a failing test and confirm it fails consistently.
3. **Root-cause** — Trace execution with debugging tools. MUST NOT fix symptoms or guess from reading code alone.
4. **Fix and verify** — Fix the root cause, confirm the failing test passes, confirm no regressions.

MUST use proper diagnostic tools (test runner output, debugger, traces, structured logs). MUST NOT debug with print/log statements alone.

## 6. Retry Discipline

- MUST undo the specific changes from a failed attempt before retrying. MUST NOT stack fix B on top of failed fix A. MUST NOT leave broken code in the tree.
- If the same approach fails 3 times after clean reverts: MUST stop, analyze the failure pattern with debugging tools, research alternatives, and record what was tried and why it failed before asking for help.
- Each retry MUST be a genuinely different approach, not a minor tweak.

## 7. Pre-Commit Verification

Before every commit, MUST pass this gate:

| Check | Command | Required result |
|---|---|---|
| Build | `dotnet build` | 0 warnings, 0 errors |
| Tests | `dotnet test` | 100% pass |

Manual review checklist before commit:

- MUST verify proper dependency direction (core has no third-party dependencies; transits/transports depend only on core plus `System.*`).
- MUST verify correct file placement in `src/` or `tests/`.
- MUST update documentation per the `docs-sync` skill when the change touches any public API, option, transport/transit behavior, or test list.
- MUST leave no `TODO`, `FIXME`, placeholder, or commented-out code.
- MUST commit only when the gate above is green. NEVER commit on red.
