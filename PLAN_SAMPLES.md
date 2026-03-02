# Samples Plan

## Group Chat Sample

### Overview

Multi-client group chat demonstrating:
- Multiple simultaneous client connections
- Message broadcasting
- Typing indicators with auto-expiry
- Join/leave notifications
- Both TCP and WebSocket transports (selectable via CLI)

### Project Structure

```
samples/
  NetConduit.Samples.GroupChat/
    Program.cs
    NetConduit.Samples.GroupChat.csproj
```

### Channel Pattern (per client)

```
Client → Server:
  - "chat" channel (messages + typing indicators)

Server → Client:
  - "broadcast" channel (all events)
```

### Message Types

```csharp
public record ChatEvent
{
    public ChatEventType Type { get; init; }
    public string Username { get; init; }
    public string? Text { get; init; }          // For Message type
    public string[]? TypingUsers { get; init; } // For TypingUpdate type
    public string[]? OnlineUsers { get; init; } // For UserList type
    public DateTime Timestamp { get; init; }
}

public enum ChatEventType
{
    Message,        // Chat message
    UserJoined,     // New user connected
    UserLeft,       // User disconnected  
    TypingStarted,  // Client → Server only
    TypingUpdate,   // Server → Client: current typing list
    UserList        // Server → Client: online users
}
```

### Typing Indicator Behavior

**Client-side:**
- On keypress → send `TypingStarted` (throttled, not spam)
- No explicit stop message

**Server-side:**
- Maintains `Dictionary<string, DateTime>` with last typing timestamp per user
- Background timer every 1 second checks for stale entries (>5 seconds old)
- Auto-removes expired users and broadcasts updated typing list

### Server Console

```
═══════════════════════════════════════════════════════════════
  NetConduit Group Chat Server (TCP)
═══════════════════════════════════════════════════════════════

[Server] Listening on port 5000...
[Server] Alice connected (1 online)
[Server] Bob connected (2 online)
[Server] Charlie connected (3 online)
[Server] Alice disconnected (2 online)

Commands: /list, /stats, /kick <user>, /quit
Server> /list
  Online users: Bob, Charlie
Server> /stats
  Connections: 2 active, 3 total
  Messages: 47 relayed
  Bytes: Sent=12,450 Received=8,230
Server> _
```

### Client Console

```
═══════════════════════════════════════════════════════════════
  NetConduit Group Chat (TCP)
═══════════════════════════════════════════════════════════════

[System] Connecting to 127.0.0.1:5000...
[System] Connected as 'Bob' (3 online: Alice, Bob, Charlie)
───────────────────────────────────────────────────────────────
[System] Alice joined
[Alice] Hey everyone!
[Charlie] Hi Alice!
[Bob] Welcome!
[System] Dave joined
[Alice] is typing...
[Alice] How's everyone doing?
[System] Charlie left
[Dave] is typing...
───────────────────────────────────────────────────────────────
Bob> hey dave
[Bob] hey dave
[Alice, Dave] are typing...
[Dave] Hello Bob!
Bob> /list
  Online: Alice, Bob, Dave
Bob> /stats
  Sent: 1,240 bytes | Received: 3,450 bytes | Uptime: 5m 32s
Bob> /quit
[System] Disconnected.
```

### Typing Indicator Display

```
No one typing:     (nothing shown)
1 person:          [Alice] is typing...
2 people:          [Alice, Bob] are typing...
3+ people:         [Alice, Bob, +2] are typing...
```

### Commands

| Command | Client | Server | Description |
|---------|--------|--------|-------------|
| `/list` | ✓ | ✓ | Show online users |
| `/stats` | ✓ | ✓ | Connection statistics |
| `/quit` | ✓ | ✓ | Disconnect and exit |
| `/kick <user>` | | ✓ | Kick a user |

### Usage

**TCP:**
```bash
# Server
dotnet run --project samples/NetConduit.Samples.GroupChat -- server tcp 5000

# Clients (multiple terminals)
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 127.0.0.1 Alice
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 127.0.0.1 Bob
```

**WebSocket:**
```bash
# Server (serves on ws://localhost:5000/chat)
dotnet run --project samples/NetConduit.Samples.GroupChat -- server ws 5000/chat

# Clients (multiple terminals)
dotnet run --project samples/NetConduit.Samples.GroupChat -- client ws 5000/chat 127.0.0.1 Alice
dotnet run --project samples/NetConduit.Samples.GroupChat -- client ws 5000/chat 127.0.0.1 Bob
```

### Implementation Checklist

- [x] Create `NetConduit.Samples.GroupChat` project
- [x] Implement `ChatEvent` and `ChatEventType` types
- [x] Implement TCP server with multi-client accept loop
- [x] Implement WebSocket server with ASP.NET Core minimal API
- [x] Implement client connection and message loop (both transports)
- [x] Implement typing indicator with 5-second auto-expiry
- [x] Implement join/leave notifications
- [x] Implement `/list`, `/stats`, `/quit` commands
- [x] Implement `/kick` command (server only)
- [x] Add to solution file
- [x] Test with multiple clients
