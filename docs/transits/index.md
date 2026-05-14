# Transits

A **transit** wraps one or two channels and exposes a higher-level abstraction.

| Transit | Package | Channels | Surface |
| --- | --- | --- | --- |
| [`StreamTransit`](stream.md) | `NetConduit.Transit.Stream` | 1 (read **or** write) | A simplex `System.IO.Stream`. |
| [`DuplexStreamTransit`](duplex-stream.md) | `NetConduit.Transit.DuplexStream` | 2 (1 read + 1 write) | A bidirectional `System.IO.Stream`. |
| [`MessageTransit<TSend,TReceive>`](message.md) | `NetConduit.Transit.Message` | 2 (1 read + 1 write) | Typed JSON messages with length-prefix framing. |
| [`DeltaMessageTransit<T>`](delta-message.md) | `NetConduit.Transit.DeltaMessage` | 2 (1 read + 1 write) | State sync via JSON deltas. |

You can use multiple transits and raw channels side by side on the same multiplexer.

## When to use which

```
have a Stream-shaped API to plug into?
  one-way:    StreamTransit
  two-way:    DuplexStreamTransit

exchanging discrete typed messages?
  always send the full payload:                MessageTransit
  syncing a frequently-changing state:         DeltaMessageTransit
```

## Common shape

Every transit ships extension methods on `IStreamMultiplexer`:

| Pattern | Returns | Behavior |
| --- | --- | --- |
| `OpenXxx(...)` | The transit (pending) | Returns immediately. Use `WaitForReadyAsync` later. |
| `OpenXxxAsync(...)` | `Task<the transit>` | Awaits readiness. |
| `AcceptXxx(...)` | The transit (pending) | Same, on the responder side. |
| `AcceptXxxAsync(...)` | `Task<the transit>` | Same, awaited. |

`MessageTransit` and `DeltaMessageTransit` need a `JsonTypeInfo<T>` argument from a source-generated `JsonSerializerContext` (or use the non-AOT overloads). See [AOT and source generators](../concepts/aot.md).

## Channel naming

`DuplexStreamTransit`, `MessageTransit`, and `DeltaMessageTransit` derive two channel IDs from a single base. The initiator opens `"{base}>>"` and accepts `"{base}<<"`; the responder reverses. Don't include `>>` or `<<` in your base IDs.

`StreamTransit` is simplex and uses the channel ID as-is.

## Disposal

Disposing a transit disposes its underlying channels. Don't share channels between transits or use them directly after handing them off.
# Transits

Transits add semantic meaning to raw [channels](../concepts/channels.md), providing higher-level messaging patterns.

## Overview

| Transit                                 | Use Case           | Description                          |
| --------------------------------------- | ------------------ | ------------------------------------ |
| [MessageTransit](message.md)            | RPC, events        | Send/receive JSON-serialized objects |
| [DeltaMessageTransit](delta-message.md)        | State sync         | Send only changed properties         |
| [DuplexStreamTransit](duplex-stream.md) | Bidirectional data | Two-way stream abstraction           |
| [StreamTransit](stream.md)              | One-way data       | Simple stream wrapper                |

## Quick Comparison

```csharp
// MessageTransit - discrete messages
await transit.SendAsync(new ChatMessage("Alice", "Hi!"));
var msg = await transit.ReceiveAsync();

// DeltaMessageTransit - state changes
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

Both MessageTransit and DeltaMessageTransit support `ReceiveAllAsync` for clean message/state loops:

```csharp
// MessageTransit
await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    HandleMessage(message);
}

// DeltaMessageTransit
await foreach (var state in DeltaMessageTransit.ReceiveAllAsync(cancellationToken))
{
    UpdateUI(state);
}
```

## Extension Methods

All transits have convenient extension methods on `IStreamMultiplexer`:

```csharp
using NetConduit.Transit.Message;

// MessageTransit
var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
var transit = await mux.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// DeltaMessageTransit
var delta = await mux.OpenDeltaMessageTransitAsync("state", GameContext.Default.GameState);
var delta = await mux.AcceptDeltaMessageTransitAsync("state", GameContext.Default.GameState);

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

| Pattern           | Write Channel | Read Channel |
| ----------------- | ------------- | ------------ |
| `Open*("chat")`   | `"chat>>"`    | `"chat<<"`   |
| `Accept*("chat")` | `"chat<<"`    | `"chat>>"`   |

The `>>` and `<<` suffixes are appended by the extension methods. You can also specify explicit channel IDs:

```csharp
var transit = await mux.OpenMessageTransitAsync(
    writeChannelId: "requests",
    readChannelId: "responses",
    sendTypeInfo: RpcContext.Default.Request,
    receiveTypeInfo: RpcContext.Default.Response);
```
