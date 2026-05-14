# Backpressure

Credit-based flow control prevents fast senders from overwhelming slow receivers. See [Concepts Overview](index.md) for related concepts.

## How It Works

Each channel has a **credit window** — the number of bytes the sender is allowed to write before waiting for the receiver to acknowledge consumption:

```
Sender                          Receiver
  │                                │
  │──── Data (consumes credit) ───▶│
  │──── Data (consumes credit) ───▶│
  │                                │
  │    [credit exhausted, waits]   │
  │                                │
  │◀─── Credit grant (ack) ────────│  (receiver processed data)
  │                                │
  │──── More data ────────────────▶│
```

## Behavior

- **Sender blocks** when credit is exhausted (backpressure applied)
- **Receiver grants credit** as it reads data from the channel
- **No data loss** — the sender simply waits until the receiver is ready
- **Per-channel** — slow Channel A doesn't affect fast Channel B

## Configuration

Credit windows are managed via slab sizes in [ChannelOptions](../api/channel-options.md):

```csharp
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "high-throughput",
    SlabSize = 4 * 1024 * 1024  // 4MB slab (larger = less frequent credit updates)
});
```

Default slab size is configured in [MultiplexerOptions](../api/multiplexer-options.md):

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,
    DefaultSlabSize = 2 * 1024 * 1024  // 2MB default for all channels
};
```

## SendTimeout

If the receiver is completely stalled and never grants credit, the sender will timeout:

```csharp
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "data",
    SendTimeout = TimeSpan.FromSeconds(60)  // Throw after 60s of no credit
});
```

Default send timeout is 30 seconds.

## Observing Backpressure

Channel statistics show bytes sent/received, which can indicate backpressure:

```csharp
var stats = channel.Stats;
// Large difference between BytesSent and peer's BytesReceived = backpressure
```
