# Scope

NetConduit is a **stream multiplexer**. It takes one bidirectional byte stream and splits it into many independent virtual channels, with framing, flow control, priority, keepalive, and reconnection.

That is the entire job. This page lists what is in scope, what is not, and why.

## In scope

| Area | What NetConduit provides |
| --- | --- |
| Framing | 8-byte header, frame and control subtypes, bounded payload size. See [Framing protocol](framing-protocol.md). |
| Channels | Open/accept rendezvous, lifecycle, write/read split. See [Channels](channels.md). |
| Backpressure | Per-channel slabs and credit-based flow control. See [Backpressure](backpressure.md). |
| Priority | Writer ordering across channels. See [Priority](priority.md). |
| Keepalive | Ping/Pong with configurable interval and timeout. See [Heartbeat](heartbeat.md). |
| Reconnection | Connect retry, session resume, write replay. See [Reconnection](reconnection.md). |
| Graceful shutdown | GoAway drain. See [Graceful shutdown](graceful-shutdown.md). |
| Transport composition | Anything that yields an `IStreamPair` plugs in. See [Transports](transports.md). |
| Transits | Optional encoding layers on top of channels. See [Transits](transits.md). |

## Out of scope

NetConduit does **not** provide, and will not add:

| Concern | Where it belongs |
| --- | --- |
| Authentication | Transport (e.g. mTLS in QUIC/WebSocket) or application. |
| Authorization | Application. |
| Encryption | Transport. NetConduit speaks plaintext over whatever stream you hand it. Use TLS/QUIC/SSH/Unix-socket permissions to protect the wire. |
| Identity, accounts, sessions (in the auth sense) | Application. The `SessionId` is a multiplexer-resume token, not an identity. |
| Hostile-peer defense | Out. NetConduit assumes both endpoints implement the protocol honestly. A malicious peer with raw wire access can desynchronize the framing; defending against that is the transport's job (authenticated framing) or the application's job (don't expose the mux to untrusted peers). |
| DoS mitigation against malicious peers | Out. Bounded slabs and frame sizes prevent unbounded growth from *honest* peers under load; they are not an adversarial defense. |
| Rate limiting, quotas | Application. |
| Service discovery, addressing, load balancing | Application or infrastructure. |
| Message routing across multiple peers | Application. NetConduit is point-to-point: one mux, one peer. |
| Persistence, durable queues | Application. Replay covers in-flight bytes across a reconnect, not durable storage. |

## Trust model

NetConduit is designed for the case where **both ends run NetConduit and the wire between them is already trusted** (TLS, loopback, Unix sockets, authenticated QUIC, a VPN, etc.).

Within that model:

- The mux validates frames against the protocol and rejects malformed input with a `MultiplexerException` and a transport-level disconnect.
- It bounds memory via slab and frame-size limits so a confused peer cannot drive unbounded allocation.
- It does not attempt to recover from a peer that violates the protocol on purpose. The connection terminates.

If your threat model includes a peer that may send hand-crafted raw frames, put authentication and integrity protection in the transport (mTLS, QUIC, signed framing) **before** NetConduit sees the bytes.

## Practical implications

- Picking a transport with TLS (QUIC, WebSocket-over-TLS, TCP-over-TLS) gives you encryption and authentication at the transport layer; NetConduit then runs over the authenticated stream.
- For IPC, OS-level permissions on the Unix socket or named pipe provide the trust boundary.
- Application-level concerns (who is the user, what can they do, how long is their token valid) live in your channels' payloads, not in the mux.
