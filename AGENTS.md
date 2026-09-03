# AGENTS.md — NetConduit

Always-on rules for agentic contributors. Code is source of truth; docs follow code.

## 1. Project map

- MUST treat `src/NetConduit/` as core: `StreamMultiplexer`, `StreamPair`, channel interfaces, framing, flow control, keepalive, reconnection.
- MUST treat `src/NetConduit.Transit.*` as optional transits: `Stream`, `DuplexStream`, `Message`, `DeltaMessage`.
- MUST treat `src/NetConduit.Transport.*` as optional transports: `Tcp`, `WebSocket`, `Udp`, `Ipc`, `Quic`.
- MUST treat `tests/NetConduit.UnitTests` + `tests/*.UnitTests` as unit tests and `tests/*.IntegrationTests` as transport integration tests.
- MUST start docs from `docs/index.md`; packages in `docs/packages.md`; samples in `samples/`.
- MUST treat version `3.1.2` in `src/Directory.Build.props` and SDK `10.0.100` in `global.json` as current. NEVER claim unreleased/breaking status from memory. MUST update these pins on every release (accepted bump tax — the value+path pair is the alignment tripwire).

## 2. Scope

- MUST treat `docs/concepts/scope.md` as authoritative for in-scope vs out-of-scope.
- MUST keep NetConduit point-to-point: one mux, one peer, one bidirectional stream into many virtual channels.
- NEVER add auth, authz, encryption, identity, hostile-peer defense, DoS mitigation, rate limiting, discovery, multi-peer routing, or durable queues to the mux. Those belong to transport or application.

## 3. Commands

- MUST use `dotnet build` to build the solution.
- MUST use `dotnet test` to run all tests.
- NEVER invent other build, publish, or run commands.

## 4. Quality gate

- MUST meet `dotnet build`: 0 warnings, 0 errors.
- MUST meet `dotnet test`: 100% pass.
- NEVER commit unless both hold.

## 5. C# standards

- MUST gate `!` on all four: proven non-null, compiler cannot infer, comment explains why safe, no restructuring alternative. Otherwise USE `?? throw new InvalidOperationException()`, `if (x is null) return;`, nullable `T?`, or `?? throw new ArgumentNullException(nameof(param))` for constructor args.
- MUST make illegal states unrepresentable; validate at boundaries; fail fast; be explicit, never implicit.
- MUST use primary constructors (C# 12) only when the constructor only stores parameters. MUST keep a traditional constructor when validation, transformation, or overloads are needed.
- MUST write XML docs (`///`) for public cross-layer surface only: `Interfaces`, `Models`, `Enums`, `Events`, `Exceptions`. MUST NOT write XML docs on `internal` types.

## 6. Commenting

- NEVER use `TODO`, `HACK`, `FIXME`, `#pragma warning disable`, or `[SuppressMessage]` (exception: `[UnconditionalSuppressMessage]` for AOT/trimming when no workaround exists).
- NEVER write conversation, meta, or obvious comments. NEVER describe what self-explanatory code does.
- MUST document why: non-obvious behavior, external spec requirements, edge cases, and reasoning.

## 7. Working agreements

- MUST prefer tools over memory: read source, list dirs, grep usages before claiming anything.
- MUST verify before claiming: reproduce with build, test, or runtime evidence. NEVER claim broken or fixed without running it.
- MUST fix docs when docs and code mismatch. NEVER change code to match docs.
- NEVER commit `investigate/` or any gitignored path.

## 8. Skills index

- MUST load the narrowest skill for the task at hand. When present, skills live in `.agents/skills/<name>/SKILL.md`.
- Available pointers: `disciplined-fix`, `docs-sync`, `dotnet-workflow`, `bug-hunt`, `arch-review`, `qa-regression`, `triage-fix`, `issue-filing`, `github-workflow`, `scope-guard`, `dev-loop`, optional `rule-authoring`.
