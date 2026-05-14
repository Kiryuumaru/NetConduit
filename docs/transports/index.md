# Transports

A transport is the single bidirectional stream the multiplexer rides on. Pick exactly one per multiplexer instance.

## Comparison

| Transport | Package | Strengths | Limitations |
| --- | --- | --- | --- |
| [TCP](tcp.md) | `NetConduit.Transport.Tcp` | Reliable, simple, ubiquitous. | Requires a reachable port. |
| [WebSocket](websocket.md) | `NetConduit.Transport.WebSocket` | HTTP-friendly, works through proxies and browsers. | A little extra framing overhead. |
| [UDP](udp.md) | `NetConduit.Transport.Udp` | Works where TCP is blocked, simple datagrams. | Built-in reliability is minimal — not a TCP replacement. |
| [IPC](ipc.md) | `NetConduit.Transport.Ipc` | Lowest latency, same-machine, no network exposure. | Single machine only. |
| [QUIC](quic.md) | `NetConduit.Transport.Quic` | TLS 1.3, multiplexing, 0-RTT. | Needs .NET 8+ and OS QUIC support. |

## Choosing one

```
need browser clients or strict HTTP environments?
  yes -> WebSocket
  no
    +- same machine?
    |    yes -> IPC
    |    no
    |     +- need TLS 1.3 + modern stack?
    |          yes -> QUIC
    |          no
    |           +- TCP available?
    |                yes -> TCP
    |                no  -> UDP
```

## Common shape

Every transport package exposes one static class with three families of helpers:

| Helper | Returns | Purpose |
| --- | --- | --- |
| `CreateOptions(...)` | `MultiplexerOptions` | Client factory. Each call opens a fresh outbound connection (supports reconnect). |
| `CreateServerOptions(...)` | `MultiplexerOptions` | Server factory. Accepts **one** connection from a listener / socket you provide. |
| (transport-specific) | various | E.g. `QuicMultiplexer.ListenAsync`, options structs like `ReliableUdpOptions`. |

A typical pair:

```csharp
// Client
var opts = TcpMultiplexer.CreateOptions("host", 5000);

// Server
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
var opts = TcpMultiplexer.CreateServerOptions(listener);
```

For servers that survive client churn, write your own factory that re-accepts from the listener — see [Reconnection](../concepts/reconnection.md#server-side-reconnection).

## What's actually inside

Every transport ultimately wraps the connection in a [`StreamPair`](../api/stream-pair.md) and hands it to the multiplexer. You can do the same with any `System.IO.Stream` (named pipes, in-memory streams, custom protocols) — see [Concepts: Transports](../concepts/transports.md#writing-a-custom-transport).
