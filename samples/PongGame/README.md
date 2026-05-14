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
