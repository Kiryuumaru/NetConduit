---
name: docs-sync
description: Use when an API, schema, test list, architecture pattern, or doc page is added, changed, or removed, or before commit. Checks and updates docs from the code as truth.
---

# Docs Sync

Keep NetConduit docs aligned with code. Code is the source of truth — you change docs, NEVER code.

## 1. Prime Directive

- MUST treat code as truth. If docs say one thing and code does another, the docs are wrong — fix the docs.
- MUST NOT invent features that do not exist in code.
- MUST NOT document aspirations — only reality.
- MUST NOT change code, ever. If you find a bug while verifying docs, note it and move on.

## 2. Real Docs Tree (Verified)

The stale paths `docs/features/*`, `docs/architecture/*`, and `TODO.md` from the old Copilot-era instructions DO NOT EXIST. NEVER reference them. Use this real tree:

- Entry: `docs/index.md` — start every docs task here.
- `README.md` (repo root) — front door; always verify first.
- `docs/getting-started.md` — install, first server and client.
- `docs/packages.md` — NuGet package contents and dependencies.
- `docs/benchmarks.md` — how to run the benchmark suite.
- `docs/concepts/` — `index.md`, `multiplexer.md`, `channels.md`, `framing-protocol.md`, `backpressure.md`, `priority.md`, `heartbeat.md`, `reconnection.md`, `graceful-shutdown.md`, `transports.md`, `transits.md`, `events.md`, `statistics.md`, `aot.md`, `scope.md` (authoritative scope).
- `docs/transports/` — `index.md` (comparison), `tcp.md`, `websocket.md`, `udp.md`, `ipc.md`, `quic.md`.
- `docs/transits/` — `index.md` (overview), `stream.md`, `duplex-stream.md`, `message.md`, `delta-message.md`.
- `docs/api/` — `index.md` plus per-type pages (`stream-multiplexer.md`, `stream-pair.md`, `read-channel.md`, `write-channel.md`, `channel-options.md`, `multiplexer-options.md`, `enums.md`, `events.md`, `errors.md`, `extensions.md`, `statistics.md`).
- `docs/samples/index.md` + per-sample `README.md` files under `samples/`.

## 3. Check-First

Before implementing, modifying, or asking questions, MUST check the relevant docs:

| Task | Check |
|---|---|
| New transit or transit change | `docs/transits/*.md` for current contract |
| New transport or transport change | `docs/transports/*.md` for current contract |
| New/changed public API | `docs/api/*.md` for current signatures |
| Behavior or lifecycle question | `docs/concepts/*.md`, starting at `docs/concepts/scope.md` |
| Sample change | `docs/samples/index.md` + the sample's `README.md` |

Documentation index: `docs/index.md`.

## 4. The Doc Loop (One File at a Time)

NEVER batch-read multiple doc files then batch-update them. Process sequentially:

```
for each doc in ordered list:
    READ    → read the single doc file completely, every line
    ANALYZE → verify every claim against actual code with tool lookups
    UPDATE  → fix the doc to match reality
    RECORD  → log findings (discrepancies, missing added, stale removed, links fixed)
    ↓
    next doc
```

### READ

- MUST read every line of the doc file. Do NOT skim.
- MUST also list its directory — check for siblings or related pages that may be stale too.

### ANALYZE

For EACH verifiable claim, MUST verify against code with tools (file reading, file listing, code search). NEVER verify from memory:

- **API signatures** — "Call `foo(bar, baz)`" → read the actual source. NEVER assume a signature.
- **Code examples** — MUST match current API signatures and behavior.
- **File paths** — "Edit `src/...`" → list the directory or search for it. NEVER assume a path exists.
- **Configuration options** — MUST match options the code actually reads.
- **Setup instructions** — MUST match `dotnet build` / `dotnet test` and real project config.
- **Architecture claims** — MUST match actual directory layout and imports.
- **Dependency claims** — MUST match actual config (version `3.1.2` in `src/Directory.Build.props`; SDK `10.0.100` in `global.json`).
- **Links** — MUST resolve. Use relative paths for internal links.

MUST also check for undocumented features: new public types, options, or behaviors in code that no doc page mentions. Note them as additions.

### UPDATE

- MUST fix inaccurate claims with what the code actually does.
- MUST remove stale content referencing things that no longer exist. NEVER leave dead content.
- MUST add missing content only if it belongs in that file's scope.
- MUST fix broken links and code examples.
- MUST preserve voice, tone, and heading hierarchy unless actively confusing. NEVER rewrite accurate prose just to change style.
- MUST keep one `h1` per file, logical `h2`/`h3` nesting, language-tagged code blocks.

### RECORD

After every file, MUST log:

```
## [filename]
### Discrepancies: N — [what the doc said vs what the code does]
### Missing added: [what]
### Stale removed: [what]
### Links fixed: N
```

## 5. Processing Priority

1. `README.md` — always first.
2. Entry-point docs (`docs/index.md`, `docs/getting-started.md`, `docs/packages.md`).
3. Setup/install docs.
4. Architecture/overview docs (`docs/concepts/`).
5. API reference (`docs/api/`).
6. Guides (`docs/transports/`, `docs/transits/`, `docs/samples/`).
7. Benchmarks (`docs/benchmarks.md`).

## 6. Required Updates (Pre-Commit Check)

| Change | Update |
|---|---|
| New/changed public type or member | `docs/api/` page for that type + examples |
| Changed option or default | Options page + `docs/getting-started.md` if shown there |
| New/changed transport | `docs/transports/<name>.md` + comparison table in `docs/transports/index.md` |
| New/changed transit | `docs/transits/<name>.md` + overview in `docs/transits/index.md` |
| Changed lifecycle/framing/flow-control behavior | Matching `docs/concepts/` page |
| New/renamed/removed tests | Test lists wherever they appear in docs |
| New sample or sample change | `docs/samples/index.md` + sample `README.md` |

Pre-commit questions — MUST answer each with YES or N/A before committing:

- Does this change any public API? → Updated `docs/api/` page.
- Does this change options or defaults? → Updated examples.
- Does this add/remove/rename tests? → Updated test lists.
- Does this change transport/transit behavior? → Updated transport/transit doc.
- Does this change architecture or lifecycle? → Updated `docs/concepts/` page.

## 7. What NOT to Do

- NEVER rewrite accurate docs just to change style.
- NEVER document a feature that does not exist in code.
- NEVER remove docs without verifying via code search that the feature is actually gone.
- NEVER batch unrelated doc edits.
- NEVER assert a claim from memory — verify every claim against code with tool lookups.
- NEVER ship a stale link or inaccurate example.
- NEVER document internal implementation details unless the page is specifically an internals or concepts guide.
- NEVER change code. Note possible code issues in your findings and move on.

## 8. Verification

- After editing a doc, MUST re-read it to confirm the change landed and the claim is still accurate against code.
- After updating a link or example, MUST verify it resolves.
- MUST verify via the project's docs tooling when available (docs build, lint, link-check). If no tooling exists, MUST explicitly record "Not run (docs-only)" rather than claiming verification.
