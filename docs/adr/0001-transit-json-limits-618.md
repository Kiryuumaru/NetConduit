# ADR-0001: Transit JSON limits (#618)

Date: 2026-09-15
Status: Accepted

## Context

`MessageTransit` and `DeltaMessageTransit` deserialize peer-supplied JSON.
The only backstop was the framing cap (`maxMessageSize`, default 16 MiB,
`src/NetConduit.Transit.Message/MessageTransit.cs:365`,
`src/NetConduit.Transit.DeltaMessage/DeltaMessageTransit.cs:731`): a payload
can fit the byte cap while being hostile in shape (nesting hundreds deep,
millions of tokens). `src/` uses no `JsonExtensionData` (verified 2026-09-15
by searching `src/`), so the CVE-2024-43485 link is indirect — but the generic
unhardened-deserialization concern stands (issue #618).

## Decision

Harden at the JSON-parse layer, behind the framing cap, in a shared helper
outside the core multiplexer package:

- `TransitJsonLimits` (`src/NetConduit.Transit.DeltaMessage/TransitJsonLimits.cs`,
  mirrored in `src/NetConduit.Transit.Message/TransitJsonLimits.cs`):
  `DefaultMaxDepth = 64` (matches the framework default; down-only — callers
  may tighten via the optional parameter, never widen past 64) and
  `DefaultMaxTokenCount = 1_000_000`.
- `JsonHardening.ParseNode` / `GateDocument`: parse with the depth cap, then
  walk-count tokens and reject past the budget.
- Adapters: Delta full-state parse
  (`DeltaMessageTransit.cs:465`), delta parse (`DeserializeDelta` at `:845`), Message
  typed/options deserialize (`MessageTransit.cs:417-440`). The 16 MiB frame
  check runs first and is untouched.
- Closed payload types only: no `$type` / `Type.GetType` on the receive path.
- AOT annotations on the surrounding methods are unchanged.

## Exception contract

Every hardening rejection throws `System.Text.Json.JsonException`.
No new public exception type. `JsonException` from parsing never
maps to a resync request: the Delta receive path parses at
`DeltaMessageTransit.cs:465` (full-state) / `:477`+`:845` (delta) outside the
apply `try` (`:486`), so only
`ApplyDelta` failures (genuine state divergence) reset the baseline and set
`_outgoingResyncPending`. Caller `JsonSerializerOptions` are cloned, never
mutated; a `JsonTypeInfo` configured deeper than 64 is rejected loudly instead
of silently widened.

## Consequences

- Payloads nested deeper than 64 or totaling over 1M tokens are rejected, even
  when under 16 MiB. Legitimate traffic — including depth-64-boundary payloads
  (pinned by tests) — is unaffected.
- Tightening either default in the future breaks compatibility for payloads in
  the tightened range; that change needs its own ADR and a major-version note.
- Follow-up (not this ADR): a P99-based tuned cap for typical payload sizes.
