# NetConduit Pong Sample

Real-time multiplayer Pong game demonstrating DeltaMessageTransit's bandwidth efficiency.

## Features

- **Real-time gameplay** - Smooth multiplayer action
- **DeltaMessageTransit** - Only changed values are sent (73-87% bandwidth savings)
- **Console UI** - Cross-platform terminal rendering

## Bandwidth Efficiency

DeltaMessageTransit sends only what changed:

| Update Type | Full State | Delta | Savings |
|-------------|------------|-------|---------|
| Full GameState | ~150 bytes | N/A | N/A |
| Position update | ~150 bytes | ~40 bytes | 73% |
| Score change | ~150 bytes | ~20 bytes | 87% |

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

# Terminal 2: Player 1
dotnet run -- client 5000 127.0.0.1

# Terminal 3: Player 2
dotnet run -- client 5000 127.0.0.1
```

## Controls

| Key | Action |
|-----|--------|
| W / Up Arrow | Move paddle up |
| S / Down Arrow | Move paddle down |
| Q / Escape | Quit |

## Architecture

```
┌─────────────────┐                      ┌─────────────────┐
│    Server       │                      │    Client       │
│                 │                      │                 │
│  Game Logic     │ DeltaMessageTransit  │   Game Render   │
│  ┌───────────┐  │   (state deltas)     │  ┌───────────┐  │
│  │ GameState │──┼─────────────────────▶│  │ GameState │  │
│  └───────────┘  │                      │  └───────────┘  │
│        ▲        │                      │        │        │
│        │        │   Player Input       │        │        │
│  Update 60fps   │◀─────────────────────┼────────┘        │
│                 │                      │                 │
└─────────────────┘                      └─────────────────┘
```

## Protocol

The server sends game state via DeltaMessageTransit:

```json
// Full state (first send)
{"ballX": 50, "ballY": 25, "p1Y": 20, "p2Y": 20, "p1Score": 0, "p2Score": 0}

// Delta (ball moved)
[0, ["ballX"], 51]
[0, ["ballY"], 26]

// Delta (score changed)
[0, ["p1Score"], 1]
```

Clients send input via MessageTransit:
```json
{"player": 1, "direction": "up"}
```

## NetConduit Features Demonstrated

| Feature | Usage |
|---------|-------|
| `DeltaMessageTransit` | Efficient state synchronization |
| `MessageTransit` | Player input commands |
| `SendAsync` | Server broadcasts state at 60fps |
| `ReceiveAllAsync` | Client receives state updates |
| Delta operations | Only changed fields transmitted |
