# Transports

NetConduit is transport-agnostic. Choose the transport that fits your use case.

## Comparison

| Transport | Package | Use Case | Pros | Cons |
|-----------|---------|----------|------|------|
| [TCP](tcp.md) | `NetConduit.Tcp` | General purpose | Reliable, simple, widely supported | Requires open port |
| [WebSocket](websocket.md) | `NetConduit.WebSocket` | Web, firewalls | HTTP-friendly, browser support | Slightly higher overhead |
| [UDP](udp.md) | `NetConduit.Udp` | Low latency | Fast, connectionless | Higher complexity |
| [IPC](ipc.md) | `NetConduit.Ipc` | Same-machine | Fastest, no network | Single machine only |
| [QUIC](quic.md) | `NetConduit.Quic` | Modern networks | 0-RTT, built-in mux | Requires .NET 9+, OS support |

## Quick Decision Guide

```
Need to work through firewalls/proxies?
├─ Yes → WebSocket
└─ No
   ├─ Same machine communication?
   │  └─ Yes → IPC (fastest)
   └─ No
      ├─ Need lowest latency (games, real-time)?
      │  └─ Yes → UDP or QUIC
      └─ No → TCP (simplest)
```

## Transport Details

- [TCP Transport](tcp.md) - Reliable TCP sockets
- [WebSocket Transport](websocket.md) - WebSocket client and server
- [UDP Transport](udp.md) - UDP with built-in reliability
- [IPC Transport](ipc.md) - Named pipes (Windows) / Unix sockets (Linux/macOS)
- [QUIC Transport](quic.md) - QUIC protocol (.NET 9+)

## Using Any Stream

NetConduit works with any bidirectional `Stream`. You can provide your own:

```csharp
// Your custom stream (must be bidirectional)
var myStream = GetMyCustomStream();

// Create options with StreamFactory
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) => (myStream, myStream)
};

var mux = StreamMultiplexer.Create(options);
```

For split read/write streams:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) => (readStream, writeStream)
};
```
