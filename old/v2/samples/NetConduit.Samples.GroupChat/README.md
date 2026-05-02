# NetConduit GroupChat Sample

Multi-user chat room with TCP and WebSocket support using MessageTransit.

## Features

- **Multi-client support** - Multiple users in the same chat room
- **TCP and WebSocket** - Choose your transport
- **User management** - User list, join/leave notifications, kick
- **Connection statistics** - View multiplexer stats

## Usage

### Start Server

```bash
# TCP server
dotnet run -- server tcp <port>

# WebSocket server
dotnet run -- server ws <port>/<path>
```

### Connect Client

```bash
# TCP client
dotnet run -- client tcp <port> <host> <username>

# WebSocket client
dotnet run -- client ws <port>/<path> <host> <username>
```

## Examples

```bash
# Terminal 1: Start TCP server
dotnet run -- server tcp 5000

# Terminal 2: Alice connects
dotnet run -- client tcp 5000 127.0.0.1 Alice

# Terminal 3: Bob connects
dotnet run -- client tcp 5000 127.0.0.1 Bob
```

### WebSocket Example

```bash
# Terminal 1: Start WebSocket server
dotnet run -- server ws 5000/chat

# Terminal 2: Connect via WebSocket
dotnet run -- client ws 5000/chat 127.0.0.1 Alice
```

## Chat Commands

| Command | Description |
|---------|-------------|
| `/list` | Show online users |
| `/stats` | Show connection statistics |
| `/quit` | Disconnect and exit |
| `/kick <user>` | (Server only) Kick a user |

## Architecture

```
┌─────────────────┐       ┌─────────────────┐       ┌─────────────────┐
│   Client 1      │       │     Server      │       │   Client 2      │
│                 │       │                 │       │                 │
│ chat channel ───┼──────▶│                 │◀──────┼─── chat channel │
│                 │       │   ChatServer    │       │                 │
│ ◀───────────────┼───────│ (broadcasts)    │───────┼───────────────▶ │
│ broadcast ch    │       │                 │       │ broadcast ch    │
└─────────────────┘       └─────────────────┘       └─────────────────┘
```

## Protocol

Chat events are JSON messages:

```json
{
  "type": "Message",
  "username": "Alice",
  "text": "Hello everyone!"
}
```

Event types: `UserJoined`, `UserLeft`, `Message`, `UserList`, `Error`, `Kicked`

## NetConduit Features Demonstrated

| Feature | Usage |
|---------|-------|
| `TcpMultiplexer` | TCP transport |
| `WebSocketMultiplexer` | WebSocket transport |
| `MessageTransit` | Typed JSON message exchange |
| `ReceiveAllAsync` | Async enumerable message loop |
| Multi-client handling | One multiplexer per connected client |
