# DeltaMessage transit

Package: [`NetConduit.Transit.DeltaMessage`](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage).

`DeltaMessageTransit<T>` sends and receives **state changes as JSON deltas**. The first send transmits the full state; subsequent sends transmit only the fields that changed. Bandwidth-efficient for high-frequency state sync (games, dashboards, telemetry).

The receiver maintains the full state locally and merges deltas as they arrive.

## API

```csharp
public sealed class DeltaMessageTransit<T> : IAsyncDisposable
{
    // Strongly typed (POCOs), AOT-safe
    public DeltaMessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonTypeInfo<T>? typeInfo,
        int maxMessageSize = 16 * 1024 * 1024);

    // Dynamic JSON (T = JsonNode / JsonObject / JsonArray / JsonDocument)
    public DeltaMessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        int maxMessageSize = 16 * 1024 * 1024);

    // From ITransit
    public bool IsReady { get; }
    public bool IsConnected { get; }
    public string? WriteChannelId { get; }
    public string? ReadChannelId { get; }
    public event EventHandler? Ready;
    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public Task WaitForReadyAsync(CancellationToken ct = default);

    public ValueTask SendAsync(T state, CancellationToken cancellationToken = default);
    public ValueTask SendBatchAsync(IEnumerable<T> states, CancellationToken cancellationToken = default);
    public ValueTask<T?> ReceiveAsync(CancellationToken cancellationToken = default);
    public IAsyncEnumerable<T?> ReceiveAllAsync(CancellationToken cancellationToken = default);
    public ValueTask RequestResyncAsync(CancellationToken cancellationToken = default);
}
```

## Extension methods

```csharp
public static class DeltaMessageTransitExtensions
{
    public static DeltaMessageTransit<T> OpenDeltaMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024);

    public static Task<DeltaMessageTransit<T>> OpenDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);

    public static DeltaMessageTransit<T> AcceptDeltaMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024);

    public static Task<DeltaMessageTransit<T>> AcceptDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);
}
```

## How deltas work

```
local                                 remote
  |  SendAsync(state1)                  |
  |  ---- full JSON of state1 -------->|  receiver = state1
  |  SendAsync(state2)                  |
  |  ---- diff(state2, state1) ------->|  receiver = state1 + diff = state2
  |  SendAsync(state3)                  |
  |  ---- diff(state3, state2) ------->|  receiver = state2 + diff = state3
  |                                     |
  |  RequestResyncAsync()               |
  |  ---- "resync please" ------------>|
  |  <--- full JSON of current state --|
```

Internally:

1. **First send.** Serialize `state` to JSON in full. Remember it as `_lastSentState`. Transmit.
2. **Subsequent sends.** Serialize `state` to JSON. Diff against `_lastSentState` (per-field). Transmit only the changed fields. Update `_lastSentState`.
3. **Receive.** First receive yields the full state. Each subsequent delta is merged into `_lastReceivedState` and the full state is yielded.

Internal delta operations include `Set`, `Remove`, `SetNull`, and `ArrayInsert` / `ArrayRemove` / `ArrayReplace` (see `NetConduit.Enums.DeltaOp`). Receivers apply them in order.

## `SendBatchAsync`

Compute a single delta against `_lastSentState` covering multiple intermediate states' worth of changes:

```csharp
await transit.SendBatchAsync(new[] { state1, state2, state3 });
```

Equivalent to `SendAsync(state3)` if intermediate states are not needed by the remote.

## `RequestResyncAsync`

Asks the **remote** to send its full state next time. Useful after the receiver knows its state is stale (e.g., after long disconnects, in tests, or when reconciling).

## Strongly typed example

```csharp
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Transit.DeltaMessage;

public class GameState
{
    public int P1Y { get; set; }
    public int P2Y { get; set; }
    public int BallX { get; set; }
    public int BallY { get; set; }
    public int ScoreP1 { get; set; }
    public int ScoreP2 { get; set; }
}

[JsonSerializable(typeof(GameState))]
internal partial class GameJson : JsonSerializerContext;
```

```csharp
// Server: stream game state
await using var transit = await mux.OpenDeltaMessageTransitAsync(
    "state",
    GameJson.Default.GameState);

var state = new GameState();
while (running)
{
    Tick(state);
    await transit.SendAsync(state);
    await Task.Delay(16);
}
```

```csharp
// Client: render
await using var transit = await mux.AcceptDeltaMessageTransitAsync(
    "state",
    GameJson.Default.GameState);

await foreach (var s in transit.ReceiveAllAsync())
    Render(s!);
```

## Dynamic example (`JsonObject`)

For schemaless or partially-typed payloads, use `JsonObject` and the parameterless constructor:

```csharp
await using var transit = await mux.OpenDeltaMessageTransitAsync<JsonObject>("leaderboard");

var board = new JsonObject
{
    ["alice"] = 100,
    ["bob"]   = 92,
};
await transit.SendAsync(board);

board["alice"] = 105;       // local mutation
await transit.SendAsync(board);   // sends only the "alice" delta
```

## Threading

`SendAsync`, `SendBatchAsync`, and `ReceiveAsync` are thread-safe; each direction is serialized by an internal semaphore.

## Limits

- Maximum frame size: `maxMessageSize` (default 16 MiB), same as `MessageTransit`.
- `T` must be JSON-serializable. For AOT, supply a `JsonTypeInfo<T>` from a source-generated `JsonSerializerContext`.
- Best for relatively small state objects with frequent updates. For arbitrary blob transfers, use [`StreamTransit`](stream.md).
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
