# Samples

Complete example applications demonstrating NetConduit features. See [Getting Started](../getting-started.md) for basic usage.

## Quick Start

All samples follow the same pattern:

```bash
cd samples/NetConduit.Samples.<Name>
dotnet run -- --help
```

## Sample Overview

| Sample | Features | Description |
|--------|----------|-------------|
| [GroupChat](../../samples/NetConduit.Samples.GroupChat) | [MessageTransit](../transits/message.md), [TCP](../transports/tcp.md), [WebSocket](../transports/websocket.md) | Multi-user chat with transport options |
| [FileTransfer](../../samples/NetConduit.Samples.FileTransfer) | Concurrent [channels](../concepts/channels.md), streaming | Parallel file transfers with progress |
| [Pong](../../samples/NetConduit.Samples.Pong) | [DeltaTransit](../transits/delta.md), real-time | Multiplayer game with efficient state sync |
| [RemoteShell](../../samples/NetConduit.Samples.RemoteShell) | Bidirectional [channels](../concepts/channels.md) | SSH-like remote command execution |
| [RpcFramework](../../samples/NetConduit.Samples.RpcFramework) | [MessageTransit](../transits/message.md), request/response | Type-safe JSON-RPC pattern |
| [TcpTunnel](../../samples/NetConduit.Samples.TcpTunnel) | [DuplexStream](../transits/duplex-stream.md), relay | Port forwarding like ngrok |

## Feature Matrix

| Sample | Transport | Transit | Key Concept |
|--------|-----------|---------|-------------|
| GroupChat | [TCP](../transports/tcp.md), [WebSocket](../transports/websocket.md) | [MessageTransit](../transits/message.md) | Multi-client server |
| FileTransfer | [TCP](../transports/tcp.md) | Raw [channels](../concepts/channels.md) | Concurrent streaming |
| Pong | [TCP](../transports/tcp.md) | [DeltaTransit](../transits/delta.md) | Bandwidth-efficient state sync |
| RemoteShell | [TCP](../transports/tcp.md) | [MessageTransit](../transits/message.md) | Multiple channel types |
| RpcFramework | [TCP](../transports/tcp.md) | [MessageTransit](../transits/message.md) | Request/response pattern |
| TcpTunnel | [TCP](../transports/tcp.md), [WebSocket](../transports/websocket.md) | [DuplexStream](../transits/duplex-stream.md), Message | Relay architecture |

## Running Samples

### GroupChat

```bash
# Server
dotnet run --project samples/NetConduit.Samples.GroupChat -- server tcp 5000

# Client
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 localhost Alice
```

### FileTransfer

```bash
# Server
dotnet run --project samples/NetConduit.Samples.FileTransfer -- server 5001

# Send files
dotnet run --project samples/NetConduit.Samples.FileTransfer -- send 5001 localhost file1.txt file2.zip
```

### Pong

```bash
# Server
dotnet run --project samples/NetConduit.Samples.Pong -- server 5000

# Client (run twice for 2 players)
dotnet run --project samples/NetConduit.Samples.Pong -- client 5000 localhost
```

### RemoteShell

```bash
# Server
dotnet run --project samples/NetConduit.Samples.RemoteShell -- server 5000

# Client
dotnet run --project samples/NetConduit.Samples.RemoteShell -- client 5000 localhost
```

### RpcFramework

```bash
# Server
dotnet run --project samples/NetConduit.Samples.RpcFramework -- server

# Client
dotnet run --project samples/NetConduit.Samples.RpcFramework -- client
```

### TcpTunnel

```bash
# Start relay
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- relay 6000 6001/relay

# Register service
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- agent localhost 6000 myservice 8080

# Forward to service
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- forward localhost 6000 myservice 4000
```

## Learning Path

1. **Start with GroupChat** - Basic MessageTransit and multi-client handling
2. **Try FileTransfer** - Raw channel streaming and concurrent operations
3. **Explore RpcFramework** - Request/response patterns
4. **Study TcpTunnel** - DuplexStream and relay architecture
5. **Examine Pong** - DeltaTransit for efficient real-time updates
