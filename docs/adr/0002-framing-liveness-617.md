# ADR-0002: Framing read liveness (#617)

Date: 2026-09-17
Status: Accepted

## Context

The framing receive path (`StreamMultiplexer.RunReaderLoopAsync`,
`src/NetConduit/StreamMultiplexer.cs:911`) had two coupled faults that let a
peer pin the reader loop indefinitely (issue #617, slowloris shape):

1. **Rent-before-bytes** — for payloads over the 64 KiB inline buffer the
   loop rented `ArrayPool<byte>.Shared.Rent(header.PayloadLength)` up front
   (`StreamMultiplexer.cs:911-915` pre-fix): allocation keyed on the
   *declared* length before a single payload byte arrived. A peer advertising
   16 MiB and dripping 1 byte/s held ~16 MiB of pooled memory per connection.
2. **Unbounded await** — the payload wait went through `ReadExactAsync`
   (`StreamMultiplexer.cs:1495-1507` pre-fix), a bare `ReadAsync` loop
   observing only the shutdown token: no deadline, no rate floor.

The hard size cap already existed (`FrameConstants.MaxFramePayloadSize`,
`src/NetConduit/Constants/FrameConstants.cs:6`, enforced at
`src/NetConduit/Internal/FrameHeader.cs:43-47`) but is size-only, not
liveness. `MultiplexerOptions` had no read-liveness knob, and no test
exercised a declared-large-but-trickled payload. Scope exception for the
observable-termination behavior change was granted by user go-ahead; the
threat model itself is unchanged (see `docs/concepts/scope.md`).

Standards applied: small cap + absolute timeout + minimum rate with grace +
oversize-is-error (RFC 9113 keepalive/ping-timeout shape, Kestrel
`Limits.MinRequestBodyDataRate`, gRPC `MinRecvPingIntervalWithoutData`
discipline). Wire format is untouched.

## Decision

Enforce liveness at the options layer, behind the unchanged single 16 MiB
cap, with incremental buffer growth:

- `MultiplexerOptions.FrameReadTimeout` (default 30 s,
  `src/NetConduit/Models/MultiplexerOptions.cs:69`): absolute per-frame
  deadline covering one header+payload iteration on the receive path. A
  stalled or slow-drip frame exceeding it throws
  `MultiplexerException(ErrorCode.Timeout)` from the reader loop
  (`StreamMultiplexer.cs:940-941` header, `:991-992` payload), which faults
  the reader task; the main loop treats it as a dead transport (channels
  marked disconnected, `Error` raised, `Disconnected(TransportError)`
  fired — `StreamMultiplexer.cs:706-757`). Oversize lengths still fail fast
  with `MultiplexerException(ErrorCode.ProtocolError)` at header parse,
  before any rent (`FrameHeader.cs:43-47`).
- `MultiplexerOptions.FrameMinReadRateBytesPerSecond` (default `0`,
  `MultiplexerOptions.cs:75`): **reserved, validated but not enforced.**
  Only `FrameReadTimeout` enforces liveness in this change. Timeout-only
  first step is deliberate: a rate floor risks breaking honest slow links,
  so the hook (option + validation) ships now and enforcement follows with
  its own ADR.
- **Sentinels:** both `Timeout.InfiniteTimeSpan` and `TimeSpan.Zero`
  disable the deadline on the receive path (`StreamMultiplexer.cs:902-909`;
  docstring `MultiplexerOptions.cs:62-68`) and on the handshake path alike
  (`MuxHandshake.cs:323`), mirroring the existing `ConnectionTimeout`
  disable idiom. Negative values other than `InfiniteTimeSpan` are rejected
  at `Create` (`StreamMultiplexer.cs:273-279`, upper-bounded by
  `ValidateTaskDelayUpperBound` like other timing knobs); negative rates are
  rejected (`:281-285`). Disabling the deadline while keepalive is also off
  (`PingInterval = Zero`) restores the pre-#617 unbounded hold — disable one
  or the other, not both, unless the transport bounds stalled reads itself.
- **Incremental growth, cap unchanged:** frames at or under 64 KiB use the
  inline buffer; larger frames rent 64 KiB first and double only as bytes
  actually arrive, capped at the declared `PayloadLength`
  (`StreamMultiplexer.cs:947-987`). Rented bytes track bytes *received*,
  never bytes *promised*; the single 16 MiB cap is unchanged and the wire
  (8-byte header, flags, limits) is byte-identical. Growth is internal, not
  tunable.
- **Handshake inherits timeout only** (`StreamMultiplexer.cs:1486-1512`
  passes `FrameReadTimeout` into `MuxHandshake.PerformInitialAsync` /
  `PerformReconnectAsync`): a stalled handshake frame throws
  `HandshakeTransportException` with a `TimeoutException` inner
  (`MuxHandshake.cs:337-342`, `:359-364`), flowing through the normal
  connect/retry path. Rate and growth do not apply to the handshake.
- **Reconnect interplay:** a liveness timeout is a transport death like any
  other — it participates in the standard `MaxAutoReconnectAttempts`
  policy (reconnect + replay when enabled; terminal `Disconnected` when
  `0`). No resync marker, no abort-and-continue: the session terminates.

## Consequences

- Stalled or slow-drip peers now disconnect (after at most one
  `FrameReadTimeout` per frame) where they previously hung the reader
  forever and pinned up to 16 MiB of pooled memory. Memory residency per
  frame is ~bytes-received, not declared length.
- Honest traffic is unaffected: full-rate frames complete under the same
  options (pinned by `FramePayloadLivenessTests`), keepalive/ping is
  untouched so idle-but-alive connections are never mistaken for stalled
  frames, and the 30 s default leaves LAN/loopback and loaded-CI peers
  wide margin.
- Tightening the default or enabling rate enforcement later breaks
  compatibility for traffic in the tightened range; that change needs its
  own ADR and a major-version note (same rule as ADR-0001).
- Follow-up (not this ADR): enforce `FrameMinReadRateBytesPerSecond` with
  a sliding window + initial grace (Kestrel-style).
