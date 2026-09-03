---
name: qa-regression
description: Use when converting bug findings into permanent regression tests. Enforces ecosystem discovery, fail-first verification, and conventional placement.
---

# QA Regression

Turn proven executable-code bugs into permanent regression tests. You write tests; you NEVER fix production code.

## 1. Prime Directive

- MUST convert every executable-code finding into a failing test. Tests assert correct behavior — the code is wrong, not the test.
- MUST run each new test and confirm it FAILS against current code before handing off. A passing test proves nothing.
- MUST NOT write unit tests for docs-only or prose-only findings — record those as no-test handoffs instead.
- MUST NOT write tests asserting behavior outside the scope in `docs/concepts/scope.md`.
- MUST NOT fix production code in this skill. Hand proven failures to `disciplined-fix`.

## 2. Test Ecosystem (Verified)

- Framework: xUnit (`[Fact]`, `Trait("Category", ...)`). MUST discover the actual patterns from existing files — NEVER guess framework conventions from memory.
- Core: `tests/NetConduit.UnitTests/` — class-per-file (e.g. `BackpressureTests`, `ConcurrencyTests`, `DataIntegrityTests`), `namespace NetConduit.UnitTests`, shared helpers like `DuplexMemoryStream`.
- Transits: `tests/NetConduit.Transit.<Name>.UnitTests/` (`Stream`, `DuplexStream`, `Message`, `DeltaMessage`) — transit tests reuse core helpers (`global using NetConduit.UnitTests`).
- Transports: `tests/NetConduit.Transport.<Name>.IntegrationTests/` (`Tcp`, `WebSocket`, `Udp`, `Ipc`, `Quic`) — real-socket tests, some with `[Fact(Timeout = ...)]`.
- Categories: `TestCategories.HighMemory` (`"HighMemory"`) and `HighMemoryHappyPath` (`"HighMemory.HappyPath"`) in `tests/NetConduit.UnitTests/TestCategories.cs`. High-memory tests join the `[Collection("HighMemory")]` collection. MUST follow the batching rules per `dotnet-workflow` Test Categorization section.
- Runner: `dotnet test` (full suite, 100% pass required); scoped runs via `dotnet test tests/<ProjectDir>`.

## 3. Discovery Before Writing

Before writing any test, MUST with tools:

1. MUST read every `investigate/*/README.md` and `poc.*` fully. NEVER work from memory of a finding.
2. MUST re-read the source files cited in the finding and confirm the behavior exists at the referenced lines.
3. MUST read existing tests in the target project to match imports, assertion style, setup/teardown, `CreatePair`/`CreateReadyPairAsync` helper patterns, and file naming.
4. MUST separate executable-code findings (get tests) from docs-only/prose-only findings (get no-test handoffs with the reason recorded).

## 4. Placement

- MUST place each test where the project already puts that kind of coverage: core mux behavior in `tests/NetConduit.UnitTests/`; transit behavior in the matching `tests/NetConduit.Transit.<Name>.UnitTests/`; transport behavior in the matching `tests/NetConduit.Transport.<Name>.IntegrationTests/`.
- MUST follow the one-class-per-file, `<Area>Tests` naming already in the repo. MUST NOT invent a new directory structure.
- MUST leverage existing helpers and fixtures (in-memory duplex, ready-pair builders) rather than duplicating them.
- MUST add `Trait("Category", ...)` only when the test genuinely needs isolated/grouped CI execution per `dotnet-workflow` Test Categorization section. MUST leave normal fast tests uncategorized.

## 5. Writing Rules

- MUST assert correct behavior — the test defines what the code SHOULD do and fails because the code is buggy.
- MUST make every test self-contained, independently runnable, deterministic, isolated, and fast (unit tests in milliseconds).
- MUST match project conventions: same usings, same assertion style, same async/cancellation patterns (`CancellationTokenSource` with timeouts, `await using` disposal).
- MUST NOT add dependencies that are not already in the project without explicitly noting it.

## 6. Naming Convention

Tests MUST read as natural QA coverage written from a spec — NEVER as artifacts of an investigation:

- MUST name like: `ChannelCloseDuringWriteCompletesCleanly`, `ReconnectReplaysUnacknowledgedWrites`, `FrameAtMaxSizeIsAccepted`, `SlowReaderAppliesBackpressure`.
- MUST NEVER use in test names, comments, or descriptions: "bug", "issue", "finding", "investigate", "replication", "reproduction", "repro", "failing", "vuln".
- MUST NEVER create any mapping file, index, or README linking tests back to investigation findings. Tests MUST be indistinguishable from the rest of the suite.

## 7. Fail-First Verification

Per `disciplined-fix` Failing-Test-First section: write, run, confirm FAILS for the predicted reason, capture output for the handoff. MUST load `disciplined-fix` for the loop

QA deltas (unique to this skill):
- MUST name every test per the Naming Convention section above — tests MUST be indistinguishable from the rest of the suite
- MUST NOT proceed to the next finding until this one either has a failing test or a documented no-test handoff — one finding at a time, NEVER batch-write

## 8. What NOT to Do

- NEVER guess the framework, import paths, or project structure — MUST find them via tools.
- NEVER write a test that passes against the buggy code.
- NEVER skip a finding because it "seems hard to test" — MUST find a way or document with evidence why it cannot be tested with available tools.
- NEVER reference bugs, issues, findings, or the `investigate/` folder in test code, names, or comments.
- NEVER hardcode a test structure — MUST derive it from the repo.
- NEVER work from memory of a file's contents — MUST re-read with tools before acting.
- NEVER fix production code here.
