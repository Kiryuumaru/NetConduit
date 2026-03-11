# DeltaTransit

Efficiently synchronize state by sending only what changed. Like git commits: only diffs, not the whole codebase. See [Transit Overview](index.md) for alternatives.

## Why DeltaTransit?

| Approach | Data Sent | Bandwidth |
|----------|-----------|-----------|
| Full state sync | `{ "score": 150, "health": 80, "items": [...], ... }` | ~500 bytes |
| Delta sync | `[0, ["score"], 150]` | ~20 bytes |

**Up to 80-95% bandwidth reduction** for incremental updates.

## Basic Usage

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define state type
public record GameState(int Score, int Health, List<string> Inventory);

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Create sender
var sender = await mux.OpenSendOnlyDeltaTransitAsync("state", GameContext.Default.GameState);

// First send - full state
var state = new GameState(100, 80, ["sword", "potion"]);
await sender.SendAsync(state);

// Update only score - sends delta
state = state with { Score = 150 };
await sender.SendAsync(state);  // Sends: [0, ["Score"], 150]

// Create receiver
var receiver = await mux.AcceptReceiveOnlyDeltaTransitAsync("state", GameContext.Default.GameState);

// Receive reconstructed state
var received = await receiver.ReceiveAsync();
Console.WriteLine(received.Score);  // 150
```

## Methods

### SendAsync

Send the current state. First call sends full state, subsequent calls send only deltas:

```csharp
await sender.SendAsync(state);
```

### SendBatchAsync

Send multiple states as a single combined delta - reduces network overhead for high-frequency updates:

```csharp
// Gaming - batch 60fps updates
var states = new[] { stateFrame1, stateFrame2, stateFrame3 };
await sender.SendBatchAsync(states);  // Single delta combining all changes
```

### ReceiveAsync

Receive the next state (blocks until available):

```csharp
var state = await receiver.ReceiveAsync(cancellationToken);
if (state is not null)
{
    UpdateUI(state);
}
```

### ReceiveAllAsync (Recommended)

**The cleanest pattern for state updates** - enumerate all states as an async stream:

```csharp
await foreach (var state in receiver.ReceiveAllAsync(cancellationToken))
{
    UpdateGameWorld(state);
}
// Loop exits when channel closes or cancellation requested
```

This is the recommended way to process state updates. Perfect for game loops, UI updates, etc.

### ResetState

Force full state transmission on next send (useful after errors or reconnection):

```csharp
sender.ResetState();  // Next SendAsync will send full state
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | True if transit has open channels |
| `WriteChannelId` | `string?` | ID of the write channel (null if receive-only) |
| `ReadChannelId` | `string?` | ID of the read channel (null if send-only) |

## Bidirectional Delta Sync

Both sides can send and receive:

```csharp
// Side A
var transitA = await mux.OpenDeltaTransitAsync("state", GameContext.Default.GameState);

// Side B
var transitB = await mux.AcceptDeltaTransitAsync("state", GameContext.Default.GameState);

// A sends to B
await transitA.SendAsync(stateA);

// B sends to A
await transitB.SendAsync(stateB);

// Both can receive with ReceiveAllAsync
_ = Task.Run(async () =>
{
    await foreach (var state in transitA.ReceiveAllAsync(ct))
        HandleStateFromB(state);
});
```

## With JsonObject (No TypeInfo Needed)

Dynamic types work without source generation:

```csharp
using System.Text.Json.Nodes;

// JsonObject doesn't need JsonTypeInfo
var writeChannel = await mux.OpenChannelAsync(new() { ChannelId = "state" });
var sender = new DeltaTransit<JsonObject>(writeChannel, null);

// Send state
var state = new JsonObject
{
    ["temperature"] = 25.5,
    ["humidity"] = 60,
    ["device"] = "sensor-1"
};
await sender.SendAsync(state);

// Update - only delta sent
state["temperature"] = 26.0;
await sender.SendAsync(state);  // Sends: [0, ["temperature"], 26.0]
```

## Delta Operations

DeltaTransit supports:

| Operation | JSON | Description |
|-----------|------|-------------|
| `Set` | `[0, ["path"], value]` | Add or update property |
| `Remove` | `[1, ["path"]]` | Remove property |
| `SetNull` | `[2, ["path"]]` | Set to null explicitly |
| `ArrayInsert` | `[3, ["path"], index, value]` | Insert at index |
| `ArrayRemove` | `[4, ["path"], index]` | Remove at index |
| `ArrayReplace` | `[5, ["path"], array]` | Replace entire array |

## Nested Properties

Delta handles nested objects:

```csharp
var state = new JsonObject
{
    ["user"] = new JsonObject
    {
        ["profile"] = new JsonObject
        {
            ["name"] = "Alice",
            ["theme"] = "light"
        }
    }
};

// Change nested property
state["user"]["profile"]["theme"] = "dark";
await sender.SendAsync(state);
// Sends: [0, ["user", "profile", "theme"], "dark"]
```

## Array Operations

```csharp
var state = new JsonObject
{
    ["items"] = new JsonArray("sword", "shield", "potion")
};

// Add item
state["items"].AsArray().Add("bow");
await sender.SendAsync(state);

// Remove item
state["items"].AsArray().RemoveAt(0);
await sender.SendAsync(state);
```

## Ordering Guarantee

DeltaTransit preserves message order (FIFO):

```csharp
// Sender
await sender.SendAsync(stateV1);
await sender.SendAsync(stateV2);
await sender.SendAsync(stateV3);

// Receiver always gets: V1 → V2 → V3
// Deltas are applied in order to reconstruct correct state
```

## Configuration

### Max Message Size

```csharp
var sender = await mux.OpenSendOnlyDeltaTransitAsync(
    "state", 
    GameContext.Default.GameState,
    maxMessageSize: 1024 * 1024);  // 1MB limit
```

## Patterns

### Game State Sync

```csharp
// Server: broadcast state to all clients
await foreach (var tick in gameLoop.TicksAsync(ct))
{
    var state = gameWorld.GetState();
    await sender.SendAsync(state);
}

// Client: receive and render
await foreach (var state in receiver.ReceiveAllAsync(ct))
{
    gameRenderer.Update(state);
}
```

### Collaborative Editing

```csharp
// User edits document
document["content"] = newContent;
await sender.SendAsync(document);

// Other users receive only the changed parts
await foreach (var doc in receiver.ReceiveAllAsync(ct))
{
    editor.ApplyUpdate(doc);
}
```

### IoT Sensor Telemetry

```csharp
// Sensor sends readings (most values don't change every tick)
while (running)
{
    var readings = sensor.GetReadings();
    await sender.SendAsync(readings);
    await Task.Delay(100);
}

// Dashboard receives only changed values
await foreach (var reading in receiver.ReceiveAllAsync(ct))
{
    dashboard.UpdateDisplay(reading);
}
```

## Tips

**Batch related changes:**
```csharp
// Good - one delta with both changes
state = state with { Score = 150, Health = 90 };
await sender.SendAsync(state);
```

**Use SendBatchAsync for high-frequency updates:**
```csharp
// Batch multiple frames worth of updates
await sender.SendBatchAsync(frameStates);
```

**Use records with `with` expression:**
```csharp
public record State(int A, int B, int C);
var newState = oldState with { A = 10 };  // Only changes A
```

**Always use `await using` for cleanup:**
```csharp
await using var sender = await mux.OpenSendOnlyDeltaTransitAsync("state", typeInfo);
```

## Thread Safety

- `SendAsync` is thread-safe (uses internal lock)
- `SendBatchAsync` is thread-safe (uses internal lock)
- `ReceiveAsync` is thread-safe (uses internal lock)
- Safe to call send and receive concurrently from different tasks

## Limitations

- Delta computation has CPU overhead (negligible for most cases)
- First sync is always full state
- Very large arrays with frequent changes may not benefit as much
