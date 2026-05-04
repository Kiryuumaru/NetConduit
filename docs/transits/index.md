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
var n = await duplex.ReadAsync(buffer);

// StreamTransit - one-way bytes
await writeStream.WriteAsync(data);
var n = await readStream.ReadAsync(buffer);
```

## ReceiveAllAsync Pattern (Recommended)

Both MessageTransit and DeltaTransit support `ReceiveAllAsync` for clean message/state loops:

```csharp
// MessageTransit
await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    HandleMessage(message);
}

// DeltaTransit
await foreach (var state in deltaTransit.ReceiveAllAsync(cancellationToken))
{
    UpdateUI(state);
}
```

## Extension Methods

All transits have convenient extension methods on `IStreamMultiplexer`:

```csharp
using NetConduit.Transits;

// MessageTransit
var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
var transit = await mux.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// DeltaTransit
var delta = await mux.OpenDeltaTransitAsync("state", GameContext.Default.GameState);
var delta = await mux.AcceptDeltaTransitAsync("state", GameContext.Default.GameState);

// StreamTransit
var stream = mux.OpenStream("file");
var stream = await mux.AcceptStreamAsync("file");

// DuplexStreamTransit
var duplex = await mux.OpenDuplexStreamAsync("tunnel");
var duplex = await mux.AcceptDuplexStreamAsync("tunnel");
```

## Open vs Accept

One side opens, the other accepts. They must pair by channel ID:

```csharp
// Side A opens
var transitA = await muxA.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Side B accepts (pairs with A's open)
var transitB = await muxB.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
```

## Channel ID Conventions

Transit extension methods automatically create paired channels:

| Pattern | Write Channel | Read Channel |
|---------|---------------|--------------|
| `Open*("chat")` | `"chat>>"` | `"chat<<"` |
| `Accept*("chat")` | `"chat<<"` | `"chat>>"` |

The `>>` and `<<` suffixes are appended by the extension methods. You can also specify explicit channel IDs:

```csharp
var transit = await mux.OpenMessageTransitAsync(
    writeChannelId: "requests",
    readChannelId: "responses",
    sendTypeInfo: RpcContext.Default.Request,
    receiveTypeInfo: RpcContext.Default.Response);
```
