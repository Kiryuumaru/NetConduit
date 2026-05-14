# Transits

A **transit** is an optional layer that wraps one or two channels and turns raw bytes into a higher-level abstraction. NetConduit ships four:

| Transit | Wraps | What you get |
| --- | --- | --- |
| [`StreamTransit`](../transits/stream.md) | One channel (read **or** write) | A simplex `System.IO.Stream` |
| [`DuplexStreamTransit`](../transits/duplex-stream.md) | One write + one read channel | A bidirectional `System.IO.Stream` |
| [`MessageTransit<TSend,TReceive>`](../transits/message.md) | One channel pair | Typed, length-prefixed JSON messages |
| [`DeltaMessageTransit<T>`](../transits/delta-message.md) | One channel pair | Stateful sync via JSON deltas |

You can use multiple transits on a single multiplexer. Each transit owns its own channel(s); they coexist with raw channels and with each other.

## Why use one

Raw channels give you a byte stream. Transits give you semantics:

- **`StreamTransit`** — simplex byte stream. Drop in anywhere a `System.IO.Stream` is expected (e.g. `FileStream.CopyToAsync`).
- **`DuplexStreamTransit`** — bidirectional `Stream` backed by two simplex channels. Useful for protocols that expect a duplex stream (`SslStream`, HTTP, RPC pipes).
- **`MessageTransit`** — discrete, typed JSON messages with length-prefix framing. The natural choice when you have a record or DTO type.
- **`DeltaMessageTransit`** — like `MessageTransit` but transmits only the **changed fields** between sends. Bandwidth-efficient for high-frequency state sync (games, dashboards, telemetry).

## The `ITransit` contract

Every transit implements `ITransit`:

```csharp
public interface ITransit : IAsyncDisposable, IDisposable
{
    bool IsReady { get; }
    bool IsConnected { get; }
    string? WriteChannelId { get; }
    string? ReadChannelId { get; }

    event EventHandler? Ready;
    event EventHandler? Connected;
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    Task WaitForReadyAsync(CancellationToken ct = default);
}
```

`IsReady` is `true` only when **all** of the transit's underlying channels are ready.

## Duplex channel naming

`DuplexStreamTransit`, `MessageTransit`, and `DeltaMessageTransit` all wrap a **pair** of channels. They derive the pair from a single base ID:

| Base ID | Initiator (Open*) | Responder (Accept*) |
| --- | --- | --- |
| `"chat"` | writes `chat>>`, reads `chat<<` | reads `chat>>`, writes `chat<<` |

Do not use `>>` or `<<` in base IDs of duplex transits — those suffixes are reserved.

`StreamTransit` is simplex and uses the channel ID you supply verbatim, on a single channel.

## Opening and accepting

Each transit ships extension methods on `IStreamMultiplexer`:

```csharp
await using var t = await mux.OpenMessageTransitAsync<MyType>("ch", AppJson.Default.MyType);
await using var t = await mux.AcceptMessageTransitAsync<MyType>("ch", AppJson.Default.MyType);
```

The `…Async` overloads return a fully ready transit. The non-`Async` overloads return immediately in pending state — call `WaitForReadyAsync` later.

See each transit's page for full signatures.

## Lifecycle

Disposing a transit disposes its underlying channels. There is no shared ownership: do not pass the same channel to two transits, and do not use a channel directly after handing it to a transit.

## AOT and JSON

`MessageTransit` and `DeltaMessageTransit` accept a source-generated `JsonTypeInfo<T>` from a `JsonSerializerContext`. This is the AOT- and trim-safe path. See [AOT and source generators](aot.md).
