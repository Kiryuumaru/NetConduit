# DeltaMessageTransit

Send only changed properties between state updates. Ideal for real-time synchronization where bandwidth efficiency matters. See [Transit Overview](index.md) for alternatives.

## How It Works

DeltaMessageTransit compares the current state to the previously sent state and transmits only the differences:

```
First send:  Full JSON object (baseline)
Subsequent:  Only changed properties (delta patch)
```

**Bandwidth savings example:**
- Full `GameState` JSON: ~150 bytes
- Typical delta (position only): ~40 bytes (73% savings)
- Score-only change: ~20 bytes (87% savings)

## Basic Usage

```csharp
using NetConduit.Transit.DeltaMessage;
using System.Text.Json.Serialization;

public class GameState
{
    public int Score { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public int Health { get; set; }
}

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Sender
var sender = mux.OpenSendOnlyDeltaMessageTransit("state", GameContext.Default.GameState);
await sender.SendAsync(new GameState { Score = 0, X = 5, Y = 10, Health = 100 });  // Full
await sender.SendAsync(new GameState { Score = 0, X = 6, Y = 10, Health = 100 });  // Only X changed
await sender.SendAsync(new GameState { Score = 10, X = 6, Y = 10, Health = 100 }); // Only Score changed

// Receiver
await using var receiver = await mux.AcceptReceiveOnlyDeltaMessageTransitAsync("state", GameContext.Default.GameState);
await foreach (var state in receiver.ReceiveAllAsync(ct))
{
    // Always receives complete reconstructed state
    UpdateUI(state);
}
```

## Methods

### SendAsync

Send a state update (automatically computes and sends delta):

```csharp
await transit.SendAsync(currentState, cancellationToken);
```

### SendBatchAsync

Send multiple state snapshots efficiently:

```csharp
await transit.SendBatchAsync(stateHistory, cancellationToken);
```

### ReceiveAsync

Receive the next state (blocks until update arrives):

```csharp
var state = await transit.ReceiveAsync(cancellationToken);
// Returns null when channel closes
```

### ReceiveAllAsync (Recommended)

Enumerate all state updates:

```csharp
await foreach (var state in transit.ReceiveAllAsync(cancellationToken))
{
    UpdateUI(state);
}
```

## Properties

| Property         | Type      | Description                                    |
| ---------------- | --------- | ---------------------------------------------- |
| `IsConnected`    | `bool`    | True if transit has open channels              |
| `WriteChannelId` | `string?` | ID of the write channel (null if receive-only) |
| `ReadChannelId`  | `string?` | ID of the read channel (null if send-only)     |

## Bidirectional

For two-way state synchronization:

```csharp
// Side A
await using var transitA = await mux.OpenDeltaMessageTransitAsync("game", GameContext.Default.GameState);
await transitA.SendAsync(myState);
var theirState = await transitA.ReceiveAsync();

// Side B
await using var transitB = await mux.AcceptDeltaMessageTransitAsync("game", GameContext.Default.GameState);
var theirState = await transitB.ReceiveAsync();
await transitB.SendAsync(myState);
```

## Send-Only / Receive-Only

Most common pattern — one side publishes state, the other consumes:

```csharp
// Publisher (no receive channel needed)
var publisher = mux.OpenSendOnlyDeltaMessageTransit("state", GameContext.Default.GameState);
await publisher.SendAsync(state);

// Subscriber (no write channel needed)
await using var subscriber = await mux.AcceptReceiveOnlyDeltaMessageTransitAsync("state", GameContext.Default.GameState);
await foreach (var state in subscriber.ReceiveAllAsync(ct))
    Render(state);
```

## Direct Construction

Construct directly with raw channels:

```csharp
var writeChannel = mux.OpenChannel("state");
var readChannel = await mux.AcceptChannelAsync("state");

// AOT-safe with JsonTypeInfo (required for POCO types)
var transit = new DeltaMessageTransit<GameState>(
    writeChannel, readChannel,
    GameContext.Default.GameState);

// Dynamic JSON types only (JsonObject, JsonNode, etc.)
var transit = new DeltaMessageTransit<JsonObject>(writeChannel, readChannel);
```

## Configuration

### Max Message Size

```csharp
var transit = await mux.OpenDeltaMessageTransitAsync(
    "state",
    GameContext.Default.GameState,
    maxMessageSize: 1024 * 1024);  // 1MB max
```

## Delta Operations

Internally, DeltaMessageTransit produces operations:

| Operation      | Description              |
| -------------- | ------------------------ |
| `Set`          | Property value changed   |
| `Remove`       | Property removed         |
| `SetNull`      | Property set to null     |
| `ArrayInsert`  | Item inserted into array |
| `ArrayRemove`  | Item removed from array  |
| `ArrayReplace` | Item replaced in array   |

The receiver automatically applies these operations to reconstruct the complete state object.
