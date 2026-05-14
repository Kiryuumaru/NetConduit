# Samples

Complete example applications demonstrating NetConduit features. See [Getting Started](../getting-started.md) for basic usage.

## Quick Start

All samples follow the same pattern:

```bash
cd samples/NetConduit.Samples.<Name>
dotnet run -- --help
```

## Sample Overview

| Sample                                                        | Features                                                                                                                    | Description                                |
| ------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------ |
| [GroupChat](../../samples/NetConduit.Samples.GroupChat)       | [MessageTransit](../transits/message.md), [TCP](../transports/tcp.md), [WebSocket](../transports/websocket.md)              | Multi-user chat with transport options     |
| [FileTransfer](../../samples/NetConduit.Samples.FileTransfer) | Concurrent [channels](../concepts/channels.md), streaming                                                                   | Parallel file transfers with progress      |
| [Pong](../../samples/NetConduit.Samples.Pong)                 | [DeltaTransit](../transits/delta.md), real-time                                                                             | Multiplayer game with efficient state sync |
| [RemoteShell](../../samples/NetConduit.Samples.RemoteShell)   | Bidirectional [channels](../concepts/channels.md)                                                                           | SSH-like remote command execution          |
| [RpcFramework](../../samples/NetConduit.Samples.RpcFramework) | [MessageTransit](../transits/message.md), request/response                                                                  | Type-safe JSON-RPC pattern                 |
| [TcpTunnel](../../samples/NetConduit.Samples.TcpTunnel)       | [DuplexStream](../transits/duplex-stream.md), relay                                                                         | Port forwarding like ngrok                 |
| [Scoreboard](../../samples/NetConduit.Samples.Scoreboard)     | [MessageTransit](../transits/message.md), [DeltaTransit](../transits/delta.md), [Reconnection](../concepts/reconnection.md) | Live leaderboard with reconnection         |

## Feature Matrix

| Sample       | Transport      | Transit                      | Key Concept                        |
| ------------ | -------------- | ---------------------------- | ---------------------------------- |
| GroupChat    | TCP, WebSocket | MessageTransit               | Multi-client server                |
| FileTransfer | TCP            | Raw channels                 | Concurrent streaming               |
| Pong         | TCP            | DeltaTransit                 | Bandwidth-efficient state sync     |
| RemoteShell  | TCP            | MessageTransit               | Multiple channel types             |
| RpcFramework | TCP            | MessageTransit               | Request/response pattern           |
| TcpTunnel    | TCP            | DuplexStream, Message        | Relay architecture                 |
| Scoreboard   | TCP            | MessageTransit, DeltaTransit | Reconnection, custom StreamFactory |

## Running Samples

### GroupChat

```bash
# Terminal 1: Start TCP server
dotnet run --project samples/NetConduit.Samples.GroupChat -- server tcp 5000

# Terminal 2: Alice connects
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 127.0.0.1 Alice

# Terminal 3: Bob connects
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 127.0.0.1 Bob
```

### FileTransfer

```bash
# Terminal 1: Start server
dotnet run --project samples/NetConduit.Samples.FileTransfer -- server 5001

# Terminal 2: Send files
dotnet run --project samples/NetConduit.Samples.FileTransfer -- send 5001 127.0.0.1 file1.txt file2.zip
```

### Pong

```bash
# Terminal 1: Start server
dotnet run --project samples/NetConduit.Samples.Pong -- server 5002

# Terminal 2: Player 1
dotnet run --project samples/NetConduit.Samples.Pong -- client 5002 127.0.0.1

# Terminal 3: Player 2
dotnet run --project samples/NetConduit.Samples.Pong -- client 5002 127.0.0.1
```

### RemoteShell

```bash
# Terminal 1: Start server
dotnet run --project samples/NetConduit.Samples.RemoteShell -- server 5003

# Terminal 2: Connect and run commands
dotnet run --project samples/NetConduit.Samples.RemoteShell -- client 5003 127.0.0.1
```

### RpcFramework

```bash
# Terminal 1: Start server
dotnet run --project samples/NetConduit.Samples.RpcFramework -- server 5004

# Terminal 2: Run client
dotnet run --project samples/NetConduit.Samples.RpcFramework -- client 5004 127.0.0.1
```

### TcpTunnel

```bash
# Terminal 1: Start relay (TCP:5000, WS:5001/relay)
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- relay 5000 5001/relay

# Terminal 2: Agent exposes local :8080 as "web"
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- agent localhost 5000 web 8080

# Terminal 3: Forward "web" to local :4000
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- forward localhost 5000 web 4000

# List available services
dotnet run --project samples/NetConduit.Samples.TcpTunnel -- list localhost 5000
```

### Scoreboard

```bash
# Terminal 1: Start server
dotnet run --project samples/NetConduit.Samples.Scoreboard -- server 5006

# Terminal 2: Run player
dotnet run --project samples/NetConduit.Samples.Scoreboard -- player 5006 localhost PlayerOne
```
