# Transits

Transits add semantic meaning to raw [channels](../concepts/channels.md), providing higher-level messaging patterns.

## Overview

| Transit | Use Case | Description |
|---------|----------|-------------|
| [MessageTransit](message.md) | RPC, events | Send/receive JSON-serialized objects |
| [DeltaTransit](delta.md) | State sync | Send only changed properties |
| [DuplexStreamTransit](duplex-stream.md) | Bidirectional data | Two-way stream abstraction |
| [StreamTransit](stream.md) | One-way data | Simple stream wrapper |

## Quick Comparison

```csharp
// MessageTransit - discrete messages
await transit.SendAsync(new ChatMessage("Alice", "Hi!"));
var msg = await transit.ReceiveAsync();

// DeltaTransit - state changes
state.Score = 150;
await transit.SendAsync(state);  // Only sends changed Score field

// DuplexStreamTransit - bidirectional bytes
await duplex.WriteAsync(data);
await duplex.ReadAsync(buffer);

// StreamTransit - one-way bytes
await writeStream.WriteAsync(data);
await readStream.ReadAsync(buffer);
```

## ReceiveAllAsync Pattern (Recommended)

Both MessageTransit and DeltaTransit support `ReceiveAllAsync` for clean message/state loops:

```csharp
// MessageTransit - handle all messages
await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    HandleMessage(message);
}

// DeltaTransit - handle all state updates
await foreach (var state in transit.ReceiveAllAsync(cancellationToken))
{
    UpdateUI(state);
}
```

This is the **recommended pattern** for message processing - cleaner than manual while loops.

## Extension Methods

All transits have convenient extension methods on `IStreamMultiplexer`:

```csharp
using NetConduit.Transits;

// Message transit (same type for send/receive)
var msgTransit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Message transit (different types)
var rpcTransit = await mux.OpenMessageTransitAsync("rpc", 
    RequestContext.Default.Request, ResponseContext.Default.Response);

// Delta transit
var deltaTransit = await mux.OpenDeltaTransitAsync("state", StateContext.Default.GameState);

// Send-only / Receive-only variants
var sender = await mux.OpenSendOnlyMessageTransitAsync("events", EventContext.Default.Event);
var receiver = await mux.AcceptReceiveOnlyMessageTransitAsync("events", EventContext.Default.Event);

// Duplex stream
var duplex = await mux.OpenDuplexStreamAsync("data");

// One-way stream
var writeStream = await mux.OpenStreamAsync("upload");
var readStream = await mux.AcceptStreamAsync("download");
```

## Channel Naming Convention

Extension methods use a consistent naming convention for bidirectional transits:

| Method | Outbound Channel | Inbound Channel |
|--------|-----------------|-----------------|
| `Open*Async("chat")` | `chat>>` | `chat<<` |
| `Accept*Async("chat")` | `chat<<` | `chat>>` |

This ensures `Open` and `Accept` pair correctly.

## Common Properties

All transits expose:

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | True if transit has open channels |
| `WriteChannelId` | `string?` | ID of the write channel |
| `ReadChannelId` | `string?` | ID of the read channel |

## Native AOT Support

All transits support Native AOT via `JsonTypeInfo`. See [API Reference](../api/index.md) for configuration options.

```csharp
// Define source-generated serialization
[JsonSerializable(typeof(MyMessage))]
public partial class MyContext : JsonSerializerContext { }

// Use with transit
var transit = await mux.OpenMessageTransitAsync("data", MyContext.Default.MyMessage);
```

Dynamic types (`JsonObject`, `JsonNode`, `JsonArray`) work without `JsonTypeInfo`.

## Thread Safety

All transits are thread-safe:
- Send operations use internal locks
- Receive operations use internal locks
- Safe to send/receive from different tasks concurrently
