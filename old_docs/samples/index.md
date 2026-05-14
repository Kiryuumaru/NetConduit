# Samples

Complete example applications demonstrating NetConduit features. See [Getting Started](../getting-started.md) for basic usage.

## Quick Start

All samples follow the same pattern:

```bash
cd samples/<SampleName>
dotnet run -- --help
```

## Sample Overview

| Sample                                                        | Features                                                                                                                    | Description                                |
| ------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------ |
| [GroupChat](../../samples/GroupChatSample)       | [MessageTransit](../transits/message.md), [TCP](../transports/tcp.md), [WebSocket](../transports/websocket.md)              | Multi-user chat with transport options     |
| [FileTransfer](../../samples/FileTransferSample) | Concurrent [channels](../concepts/channels.md), streaming                                                                   | Parallel file transfers with progress      |
| [Pong](../../samples/PongGame)                 | [DeltaMessageTransit](../transits/delta-message.md), real-time                                                                             | Multiplayer game with efficient state sync |
| [RemoteShell](../../samples/RemoteShellSample)   | Bidirectional [channels](../concepts/channels.md)                                                                           | SSH-like remote command execution          |
| [RpcFramework](../../samples/RpcFrameworkSample) | [MessageTransit](../transits/message.md), request/response                                                                  | Type-safe JSON-RPC pattern                 |
| [TcpTunnel](../../samples/SimpleTcpTunnel)       | [DuplexStream](../transits/duplex-stream.md), relay                                                                         | Port forwarding like ngrok                 |
| [Scoreboard](../../samples/ScoreboardSample)     | [MessageTransit](../transits/message.md), [DeltaMessageTransit](../transits/delta-message.md), [Reconnection](../concepts/reconnection.md) | Live leaderboard with reconnection         |

## Feature Matrix

| Sample       | Transport      | Transit                      | Key Concept                        |
| ------------ | -------------- | ---------------------------- | ---------------------------------- |
| GroupChat    | TCP, WebSocket | MessageTransit               | Multi-client server                |
| FileTransfer | TCP            | Raw channels                 | Concurrent streaming               |
| Pong         | TCP            | DeltaMessageTransit                 | Bandwidth-efficient state sync     |
| RemoteShell  | TCP            | MessageTransit               | Multiple channel types             |
| RpcFramework | TCP            | MessageTransit               | Request/response pattern           |
| TcpTunnel    | TCP            | DuplexStream, Message        | Relay architecture                 |
| Scoreboard   | TCP            | MessageTransit, DeltaMessageTransit | Reconnection, custom StreamFactory |

## Running Samples

### GroupChat

```bash
# Terminal 1: Start TCP server
dotnet run --project samples/GroupChatSample -- server tcp 5000

# Terminal 2: Alice connects
dotnet run --project samples/GroupChatSample -- client tcp 5000 127.0.0.1 Alice

# Terminal 3: Bob connects
dotnet run --project samples/GroupChatSample -- client tcp 5000 127.0.0.1 Bob
```

### FileTransfer

```bash
# Terminal 1: Start server
dotnet run --project samples/FileTransferSample -- server 5001

# Terminal 2: Send files
dotnet run --project samples/FileTransferSample -- send 5001 127.0.0.1 file1.txt file2.zip
```

### Pong

```bash
# Terminal 1: Start server
dotnet run --project samples/PongGame -- server 5002

# Terminal 2: Player 1
dotnet run --project samples/PongGame -- client 5002 127.0.0.1

# Terminal 3: Player 2
dotnet run --project samples/PongGame -- client 5002 127.0.0.1
```

### RemoteShell

```bash
# Terminal 1: Start server
dotnet run --project samples/RemoteShellSample -- server 5003

# Terminal 2: Connect and run commands
dotnet run --project samples/RemoteShellSample -- client 5003 127.0.0.1
```

### RpcFramework

```bash
# Terminal 1: Start server
dotnet run --project samples/RpcFrameworkSample -- server 5004

# Terminal 2: Run client
dotnet run --project samples/RpcFrameworkSample -- client 5004 127.0.0.1
```

### TcpTunnel

```bash
# Terminal 1: Start relay (TCP:5000, WS:5001/relay)
dotnet run --project samples/SimpleTcpTunnel -- relay 5000 5001/relay

# Terminal 2: Agent exposes local :8080 as "web"
dotnet run --project samples/SimpleTcpTunnel -- agent localhost 5000 web 8080

# Terminal 3: Forward "web" to local :4000
dotnet run --project samples/SimpleTcpTunnel -- forward localhost 5000 web 4000

# List available services
dotnet run --project samples/SimpleTcpTunnel -- list localhost 5000
```

### Scoreboard

```bash
# Terminal 1: Start server
dotnet run --project samples/ScoreboardSample -- server 5006

# Terminal 2: Run player
dotnet run --project samples/ScoreboardSample -- player 5006 localhost PlayerOne
```
