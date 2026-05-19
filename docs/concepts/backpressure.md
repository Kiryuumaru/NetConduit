# Backpressure

NetConduit uses **per-channel slab buffers with credit-based flow control**. Slow consumers slow down their producer without blocking other channels on the same multiplexer.

## How it works

Every channel has a fixed-size ring buffer ("slab") on each side:

- **Writer side.** `WriteAsync` copies your bytes into the slab and queues a `Data` frame. If the slab is full, `WriteAsync` waits — up to `SendTimeout` — for space to free up.
- **Reader side.** Incoming `Data` frames land in the slab. `ReadAsync` drains it. When reconnection replay is disabled (`MaxAutoReconnectAttempts = 0`), the writer treats sent data as acknowledged immediately, freeing slab space as frames leave the wire. When replay is enabled, the writer retains sent data for replay and slab space is bounded by the slab size.

Because each channel has its own slab, a stalled `chat` channel can't block an `uploads` channel that's still draining.

## Tuning slab size

```csharp
mux.OpenChannel(new ChannelOptions
{
    ChannelId = "uploads",
    SlabSize  = 4 * 1024 * 1024,   // 4 MiB
});
```

| Constant | Value |
| --- | --- |
| `MinSlabSize` | 64 KiB |
| `DefaultSlabSize` | 1 MiB |
| `MaxSlabSize` | 64 MiB |

Larger slab:

- More data in flight per channel → higher peak throughput on high-latency links.
- More memory per channel.

Smaller slab:

- Tighter pushback on fast producers.
- Lower memory.

Pick larger for bulk transfers, default for general traffic, smaller for many short-lived channels.

## `SendTimeout`

```csharp
new ChannelOptions
{
    ChannelId   = "uploads",
    SendTimeout = TimeSpan.FromSeconds(60),
}
```

`SendTimeout` caps how long `WriteAsync` will wait for slab space. On timeout it throws — typically `OperationCanceledException` from the inner CTS, or a `MultiplexerException` with `ErrorCode.Timeout` for protocol-level timeouts. Default: 30 seconds.

## What `WriteAsync` actually returns

`WriteAsync` returns when your bytes are **enqueued into the slab** — not when they hit the wire. To force a flush to the transport, call `mux.FlushAsync()`.

## Reads return early

`ReadAsync(Memory<byte> buffer)` returns the number of bytes available, which may be **less** than the buffer size. Loop until you have what you need, or use the channel's `Stream` adapter (`ch.AsStream()`), which loops for you.

A return of `0` means EOF: the channel is closed and no more data will arrive.

## Coordinating with priorities

When several channels have data ready, the writer thread picks the next frame by priority (see [Priority](priority.md)). Backpressure on one channel doesn't reorder others — they continue to be picked in priority order.

## When slabs aren't enough

For very large, monotonic transfers (multi-GB files), prefer:

- Many smaller messages on a single channel — each `WriteAsync` is its own credit step.
- Multiple channels in parallel — one per file or shard.

The [File Transfer sample](../../samples/FileTransferSample/README.md) shows the per-file-channel pattern.
