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

    // Dynamic JSON (T = JsonNode / JsonObject / JsonArray / JsonDocument / JsonElement)
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

    public static DeltaMessageTransit<T> OpenDeltaMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        int maxMessageSize = 16 * 1024 * 1024);

    public static Task<DeltaMessageTransit<T>> OpenDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);

    public static Task<DeltaMessageTransit<T>> OpenDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);

    public static DeltaMessageTransit<T> AcceptDeltaMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024);

    public static DeltaMessageTransit<T> AcceptDeltaMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        int maxMessageSize = 16 * 1024 * 1024);

    public static Task<DeltaMessageTransit<T>> AcceptDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);

    public static Task<DeltaMessageTransit<T>> AcceptDeltaMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);
}
```

Overloads that take `JsonTypeInfo<T>` are AOT-safe and intended for POCO state types. Overloads without `JsonTypeInfo<T>` use the dynamic JSON constructor and are intended for `JsonNode`, `JsonObject`, `JsonArray`, `JsonDocument`, and `JsonElement`.

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
