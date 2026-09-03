---
name: scope-guard
description: Use when determining whether work is in scope, or when sweeping a tracker for out-of-scope issues. Enforces the 6-check Scope Test and opt-in sweep discipline.
---

# Scope Guard

Know what NetConduit is and is not before acting. Scope decides: file or discard, fix or close, test or drop, document or remove, build or push back.

## 1. Scope Source

- MUST treat `docs/concepts/scope.md` as authoritative. It is the dedicated scope file — cite it directly, NEVER re-derive scope from scattered hints.
- NetConduit is a **stream multiplexer**: one bidirectional byte stream into many independent virtual channels, with framing, flow control, priority, keepalive, and reconnection. That is the entire job.
- In scope: framing, channels, backpressure, priority, keepalive, reconnection (retry, resume, replay), graceful shutdown, transport composition via `IStreamPair`, transits.
- Out of scope (will not add): authentication, authorization, encryption, identity/accounts/sessions in the auth sense, hostile-peer defense, DoS mitigation against malicious peers, rate limiting/quotas, service discovery/addressing/load balancing, multi-peer routing (point-to-point only: one mux, one peer), persistence/durable queues.
- Trust model: both ends run NetConduit honestly over an already-trusted wire. Findings requiring a malicious peer with raw wire access are out of scope.
- If `docs/concepts/scope.md` is ambiguous or contradicts other docs on the point at hand, MUST pause and ask the user. MUST NEVER proceed on a guessed scope, close issues on it, or write tests against it.

## 2. Scope Test (6 Checks)

Before ANY finding becomes an issue, fix, test, doc page, or architectural recommendation, it MUST pass ALL six checks against `docs/concepts/scope.md`:

1. **Inside the supported feature surface** — the behavior belongs to a listed in-scope area.
2. **Not an explicit non-goal** — the behavior is not in the out-of-scope table.
3. **Not a category mismatch** — e.g. no "missing web UI" against a library, no "missing CLI flag" against the mux core, no "runtime crash" against a docs-only change.
4. **Not a feature request in disguise** — "it should also do X" where X is promised nowhere is a roadmap item, not a bug.
5. **Within supported environments** — reproduces on a supported runtime, OS, dependency version, and configuration (version `3.1.2`, SDK `10.0.100`).
6. **Belongs to this repo** — not actually about an upstream library, a downstream consumer, or another repo.

A finding failing ANY check is out of scope. Disposition by consumer:

| Consumer | Disposition |
|---|---|
| Issue filing | Discard with a one-line reason; MUST NOT file. |
| Triage / tracker sweep | Close via the opt-in Scope Sweep section below |
| Bug hunt / QA / review | Record and drop; MUST NOT pursue. |
| Documentation | The docs are wrong, not the feature missing — remove or correct the docs. |
| New feature request | Confirm in-scope or get explicit user approval to expand scope. |

## 3. Scope Sweep (Opt-In, Write-Gated)

The Sweep closes open issues that fail the Scope Test. It is OPT-IN and gated — MUST satisfy ALL preconditions or MUST NOT close anything:

1. `docs/concepts/scope.md` is unambiguous on the points being closed on.
2. Tracker write access is verified.
3. The user has authorized the sweep for this session.

Per issue:

1. MUST read the full issue — title, body, every comment. Title alone is never enough.
2. MUST run the 6-check Scope Test. Passes all six → leave open; it is a fix target, not a closure candidate.
3. MUST confirm high-confidence failure citable to a specific `docs/concepts/scope.md` line. If uncertain → MUST NOT close; surface to the user instead.
4. MUST post an individual polite comment BEFORE closing:

   ```markdown
   Thanks for taking the time to file this, @<reporter>.

   After reviewing, this falls outside the project's current scope:

   > <Which Scope Test check failed and why, quoting docs/concepts/scope.md.>

   Source: docs/concepts/scope.md:<line>

   I'm going to close this as **not planned** — but if you think the scope was
   misread, please reopen with a pointer to where this *is* declared supported
   and we'll re-evaluate. Thanks again.
   ```

5. MUST close with reason `not planned` — NEVER `completed`.
6. MUST record each closure: issue number, URL, failed check, scope citation, comment URL.

## 4. Sweep Hard Rules

- MUST NEVER bulk-close. Every closure gets its own individual comment — no mass "closing N issues as out of scope" comments.
- MUST NEVER close in-scope issues via the sweep — those are fixed and closed by the merging PR's `Fixes #N` (see `github-workflow`).
- MUST NEVER close as `completed`. Out-of-scope means `not planned`.
- MUST NEVER close ambiguous issues. Surface them.
- MUST NEVER re-close an issue someone re-opened in the same session. Surface it.
- MUST NEVER silently override declared scope. If a closure feels right but no source line supports it, the scope doc needs updating first — with user approval, not stealth.
- MUST NEVER close issues without write access — record them for the user to close manually instead.
- MUST NEVER use closure to silence inconvenient reports — closure requires a Scope Test failure backed by a citable source.

## 5. Quick Reference

| About to… | First check |
|---|---|
| File a bug | Scope Test section above — all six checks MUST pass. |
| Close a bug as out-of-scope | Scope Sweep section above — preconditions, individual comment, `not planned`. |
| Write a test | Behavior MUST be in scope, else drop it. |
| Recommend an architecture change | MUST support an in-scope use case, never a non-goal. |
| Document a feature | Feature MUST be in scope; else the docs are wrong. |
| Implement a new feature | MUST be in scope or have explicit user approval. |
| Pursue a suspicion | MUST confirm the affected behavior is in scope first. |
