# WriteChannel

A channel for sending data to the remote side. Inherits from `Stream`, so it works with any API that writes to streams.

## Basic Usage

Open a channel and write data:

```csharp
var channel = await mux.OpenChannelAsync("data");
await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello!"));
await channel.CloseAsync();
```

## Stream Behavior

`WriteChannel` is a **write-only stream**. Attempting to read, seek, or get length throws `NotSupportedException`.

| Property | Value |
|----------|-------|
| `CanRead` | `false` |
| `CanWrite` | `true` (while open) |
| `CanSeek` | `false` |

### WriteAsync

Writes data to the channel, waiting for credits if needed:

```csharp
ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
```

If the receiver hasn't granted enough credits, `WriteAsync` blocks until credits are available or `SendTimeout` expires.

The synchronous `Write(byte[], int, int)` method is also available but blocks the calling thread.

### FlushAsync

Forces buffered data to be sent immediately:

```csharp
await channel.FlushAsync(cancellationToken);
```

### CloseAsync

Gracefully closes the channel, sending a FIN frame to the remote side:

```csharp
await channel.CloseAsync(cancellationToken);
```

The remote `ReadChannel` will receive `0` bytes (EOF) after reading all buffered data.

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ChannelId` | `string` | The channel identifier |
| `State` | `ChannelState` | Current state: `Opening`, `Open`, `Closing`, `Closed` |
| `Priority` | `ChannelPriority` | Channel priority level — affects frame scheduling |
| `AvailableCredits` | `long` | Current send credits — bytes you can write before blocking |
| `CloseReason` | `ChannelCloseReason?` | Why the channel closed (`null` while open) |
| `CloseException` | `Exception?` | Exception that caused closure, if any |
| `Stats` | `ChannelStats` | Per-channel statistics — see [Statistics](statistics.md) |

### AvailableCredits

Shows how many bytes you can write immediately without waiting:

```csharp
Console.WriteLine($"Credits: {channel.AvailableCredits} bytes available");
```

When credits reach 0, the next `WriteAsync` call blocks until the receiver grants more. See [Backpressure](../concepts/backpressure.md).

## Events

### OnClosed

Fires when the channel closes for any reason:

```csharp
channel.OnClosed += (reason, ex) =>
{
    Console.WriteLine($"Channel {channel.ChannelId} closed: {reason}");
};
```

### OnCreditStarvation

Fires when a write is blocked because no credits are available:

```csharp
channel.OnCreditStarvation += () =>
{
    Console.WriteLine($"Channel {channel.ChannelId}: waiting for credits...");
};
```

This indicates the receiver is slower than the sender. Consider reducing send rate or increasing `MaxCredits`.

### OnCreditRestored

Fires when credits become available after a starvation period:

```csharp
channel.OnCreditRestored += waitDuration =>
{
    Console.WriteLine($"Channel {channel.ChannelId}: credits restored after {waitDuration.TotalMilliseconds}ms");
};
```

## Credit-Based Flow Control

WriteChannel participates in [backpressure](../concepts/backpressure.md). The receiver controls how much data you can send by granting credits.

Configure credit behavior per channel:

```csharp
var channel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "bulk-transfer",
    MinCredits = 128 * 1024,     // 128 KB — replenish threshold
    MaxCredits = 8 * 1024 * 1024, // 8 MB — maximum window
    SendTimeout = TimeSpan.FromSeconds(60)
});
```

| Option | Default | Description |
|--------|---------|-------------|
| `MinCredits` | 64 KB | Credits replenished when usage drops below this |
| `MaxCredits` | 4 MB | Maximum credits granted |
| `SendTimeout` | 30 seconds | How long `WriteAsync` waits for credits before throwing |

### Credit Statistics

Monitor credit behavior via `ChannelStats`:

```csharp
var stats = channel.Stats;
Console.WriteLine($"Credits granted: {stats.CreditsGranted}");
Console.WriteLine($"Credits consumed: {stats.CreditsConsumed}");
Console.WriteLine($"Starvation count: {stats.CreditStarvationCount}");
Console.WriteLine($"Total wait time: {stats.TotalWaitTimeForCredits}");
Console.WriteLine($"Longest wait: {stats.LongestWaitForCredits}");
Console.WriteLine($"Currently waiting: {stats.IsWaitingForCredits}");
```

## Priority

Higher-priority channels get their frames sent first when the multiplexer has frames from multiple channels queued:

```csharp
var controlChannel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "control",
    Priority = ChannelPriority.Highest
});

var dataChannel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "data",
    Priority = ChannelPriority.Low
});
```

See [Priority](../concepts/priority.md).

## Patterns

### Write and Close

```csharp
var channel = await mux.OpenChannelAsync("request");
await channel.WriteAsync(requestBytes);
await channel.CloseAsync(); // Signal: "I'm done sending"
```

### Stream Large Data

```csharp
var channel = await mux.OpenChannelAsync("file");
await using var file = File.OpenRead("large-file.dat");
await file.CopyToAsync(channel);
await channel.CloseAsync();
```

### Use with StreamWriter

```csharp
var channel = await mux.OpenChannelAsync("log");
await using var writer = new StreamWriter(channel, leaveOpen: true);
await writer.WriteLineAsync("Log entry 1");
await writer.WriteLineAsync("Log entry 2");
await writer.FlushAsync();
await channel.CloseAsync();
```

### Adaptive Send Rate

```csharp
channel.OnCreditStarvation += () => reduceSendRate = true;
channel.OnCreditRestored += _ => reduceSendRate = false;

while (hasData)
{
    var data = GetNextChunk(reduceSendRate ? smallChunkSize : largeChunkSize);
    await channel.WriteAsync(data, cancellationToken);
}
```

## Disposal

Disposing a `WriteChannel` closes it (sends FIN):

```csharp
await channel.DisposeAsync();
// or: channel.Dispose();
```

Prefer explicit `CloseAsync()` followed by dispose for clarity.

## See Also

- [ReadChannel](read-channel.md) — The receiving counterpart
- [Channels](../concepts/channels.md) — Channel concepts and patterns
- [Backpressure](../concepts/backpressure.md) — Credit-based flow control
- [Priority](../concepts/priority.md) — Frame scheduling
- [Statistics](statistics.md) — Per-channel metrics
