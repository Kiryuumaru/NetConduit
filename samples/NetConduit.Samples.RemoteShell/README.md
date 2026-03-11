# NetConduit RemoteShell Sample

SSH-like remote command execution with persistent shell sessions.

## Features

- **Persistent shell** - Maintains shell state across commands
- **Cross-platform** - Works with cmd.exe (Windows) or bash (Linux/macOS)
- **Bidirectional streams** - Real-time stdout/stderr streaming
- **Ctrl+C support** - Send interrupt signals to remote shell

## Usage

### Start Server

```bash
dotnet run -- server <port>
```

### Connect Client

```bash
dotnet run -- client <port> <host>
```

## Examples

```bash
# Terminal 1: Start server
dotnet run -- server 5000

# Terminal 2: Connect client
dotnet run -- client 5000 127.0.0.1

# Now you have a remote shell:
> cd /tmp
> pwd
/tmp
> echo "Hello from remote!"
Hello from remote!
```

## Architecture

```
┌─────────────────────┐                    ┌─────────────────────┐
│       Client        │                    │       Server        │
│                     │                    │                     │
│  ┌───────────────┐  │    cmd channel     │  ┌───────────────┐  │
│  │ Console Input │──┼───────────────────▶│──│ Shell stdin   │  │
│  └───────────────┘  │                    │  └───────────────┘  │
│                     │                    │         │           │
│  ┌───────────────┐  │    out channel     │         ▼           │
│  │ Console Output│◀─┼────────────────────┼──│ Shell Process │  │
│  └───────────────┘  │                    │  │ (cmd/bash)    │  │
│                     │                    │  └───────────────┘  │
│  ┌───────────────┐  │   ctrl channel     │                     │
│  │ Ctrl+C Handler│──┼───────────────────▶│──│ Process Signal│  │
│  └───────────────┘  │                    │  └───────────────┘  │
└─────────────────────┘                    └─────────────────────┘
```

## Channels

| Channel | Direction | Purpose |
|---------|-----------|---------|
| `cmd` | Client → Server | Command input (stdin) |
| `out` | Server → Client | Shell output (stdout/stderr) |
| `ctrl` | Client → Server | Control signals (Ctrl+C) |

## Client Commands

| Command | Description |
|---------|-------------|
| `exit` | Close connection |
| Ctrl+C | Send interrupt to remote shell |

## NetConduit Features Demonstrated

| Feature | Usage |
|---------|-------|
| `MessageTransit` | Typed command/control messages |
| `OpenChannelAsync` | Multiple purpose-specific channels |
| `AcceptChannelsAsync` | Server accepts client channels |
| Bidirectional comms | Separate channels for input/output |
| Real-time streaming | Shell output streamed immediately |
