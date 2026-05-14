# PongGame

A two-player networked Pong rendered in the console. Demonstrates mixing transits on a single multiplexer for a real-time game loop:

- **Inputs** flow client -> server as discrete messages.
- **Game state** flows server -> client as deltas (only the changed fields).

## What it shows

- `MessageTransit<InputMessage, InputMessage>` for paddle-up / paddle-down events.
- `DeltaMessageTransit<GameState>` for the shared world (ball position, scores, paddles) at ~60 Hz.
- Stable bandwidth even at high tick rates because most ticks only change a handful of fields.

## Topology

```
  client  --- "input"  msg  -->  server   (paddle commands)
  client  <-- "state"  delta --  server   (60 Hz state diff)
```

## Run

```powershell
# Terminal 1
dotnet run --project samples/PongGame -- server 5000

# Terminal 2
dotnet run --project samples/PongGame -- client 5000 127.0.0.1
```

| Arg | Server | Client |
| --- | --- | --- |
| 1 | `server` | `client` |
| 2 | port | port |
| 3 | — | host |

## Key code shape

```csharp
// Server: stream state at 60Hz
var state = new GameState();
while (running)
{
    PhysicsTick(state);
    await stateTransit.SendAsync(state);   // sends only delta
    await Task.Delay(16);
}
```

```csharp
// Client: render and capture input
_ = Task.Run(async () => {
    await foreach (var s in stateTransit.ReceiveAllAsync())
        Render(s!);
});

while (true)
{
    var key = Console.ReadKey(true);
    await inputTransit.SendAsync(new InputMessage(key.Key));
}
```
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
