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
