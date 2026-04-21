# NetConduit Security & QA Investigation

52 tests across 8 bug investigations. All bugs are **proven with runnable code**.

```
dotnet test investigate/NetConduit.Investigate
```

## Findings Summary

| # | Severity | Finding | CWE |
|---|----------|---------|-----|
| 001 | HIGH | DeltaTransit non-atomic write — message framing corruption on transport failure | CWE-354 |
| 002 | MEDIUM | Channel suffix collision — IDs containing ">>" or "<<" produce ambiguous names | CWE-20 |
| 003 | HIGH | No MaxFrameSize validation — negative/zero values break protocol or allow 2GB DoS | CWE-20, CWE-400 |
| 004 | MEDIUM | Handshake nonce uses non-cryptographic PRNG — predictable index space | CWE-330, CWE-338 |
| 005 | MEDIUM | No options validation — MinCredits > MaxCredits corrupts flow control | CWE-20 |
| 006 | HIGH | WebSocket session routing — no authentication, session hijacking via GUID | CWE-287 |
| 007 | MEDIUM | UDP payload length truncated to UInt16 — silent data loss with large MTU | CWE-197, CWE-681 |
| 008 | HIGH | No channel count limit — unbounded resource consumption by malicious peer | CWE-770 |

## Directory Structure

```
investigate/
├── README.md                              ← This file
├── 001-delta-transit-nonatomic-write/
│   └── README.md                          ← Bug story & evidence
├── 002-channel-suffix-collision/
│   └── README.md
├── 003-maxframesize-bypass/
│   └── README.md
├── 004-nonce-predictability/
│   └── README.md
├── 005-options-no-validation/
│   └── README.md
├── 006-websocket-session-hijack/
│   └── README.md
├── 007-udp-payload-truncation/
│   └── README.md
├── 008-unbounded-channel-open/
│   └── README.md
└── NetConduit.Investigate/                ← Test project with reproduction code
    ├── NetConduit.Investigate.csproj
    ├── Helpers/
    │   └── DuplexPipe.cs
    ├── DeltaTransitNonAtomicWriteTest.cs
    ├── ChannelSuffixCollisionTest.cs
    ├── MaxFrameSizeBypassTest.cs
    ├── HandshakeNoncePredictabilityTest.cs
    ├── OptionsValidationTest.cs
    ├── WebSocketSessionHijackTest.cs
    ├── UdpPayloadTruncationTest.cs
    └── UnboundedChannelOpenTest.cs
```

## Methodology

1. **Source code review** of all 54 source files across core library and 5 transports
2. **CWE/OWASP reference** for each finding category
3. **Reproduction tests** that prove each bug without assumptions
4. Each bug folder contains a `README.md` with:
   - Exact code locations with line references
   - Proof-of-concept logic or attack scenario
   - CWE classification
   - Severity assessment
   - **Recommended fix** with concrete code examples

## Severity Definitions

| Level | Meaning |
|-------|---------|
| HIGH | Exploitable by remote peer, causes data corruption, DoS, or session hijack |
| MEDIUM | Requires misconfiguration or specific conditions, causes logic errors or data loss |
