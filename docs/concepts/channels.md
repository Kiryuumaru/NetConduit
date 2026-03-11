# Channels

Channels are virtual one-way streams over a single physical connection. See [Concepts Overview](index.md) for related concepts.

## Simplex Design

Channels are **simplex** (one-way):

| Channel Type | Created By | Direction |
|--------------|------------|-----------|
| `WriteChannel` | `OpenChannelAsync()` | You → Remote |
| `ReadChannel` | `AcceptChannelAsync()` | Remote → You |

```csharp
// Side A opens - gets WriteChannel
var send = await muxA.OpenChannelAsync(new() { ChannelId = "data" });

// Side B accepts - gets ReadChannel
var receive = await muxB.AcceptChannelAsync("data");

// A writes, B reads
await send.WriteAsync(data);
var n = await receive.ReadAsync(buffer);
```

## Channel IDs

Channel IDs are strings (max 1024 bytes UTF-8):

```csharp
// Simple names
var ch1 = await mux.OpenChannelAsync(new() { ChannelId = "control" });
var ch2 = await mux.OpenChannelAsync(new() { ChannelId = "data" });

// Structured names
var ch3 = await mux.OpenChannelAsync(new() { ChannelId = "user/123/messages" });
var ch4 = await mux.OpenChannelAsync(new() { ChannelId = $"file-{Guid.NewGuid()}" });
```

Each channel ID must be unique per direction:
- You can have `OpenChannelAsync("data")` and `AcceptChannelAsync("data")` (different directions)
- You cannot have two `OpenChannelAsync("data")` (same direction, same ID)

## Channel as Stream

Channels inherit from `Stream`:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "text" });

// Standard Stream methods
await channel.WriteAsync(data);
await channel.FlushAsync();
var n = await channel.ReadAsync(buffer);

// Use with StreamReader/Writer
using var writer = new StreamWriter(channel, leaveOpen: true);
await writer.WriteLineAsync("Hello!");

// Use with CopyToAsync
await sourceStream.CopyToAsync(channel);
```

## Opening Channels

```csharp
// Basic open
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

// With options
var channel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High,
    MinCredits = 128 * 1024,
    MaxCredits = 8 * 1024 * 1024,
    SendTimeout = TimeSpan.FromSeconds(60)
});

// With cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var channel = await mux.OpenChannelAsync(options, cts.Token);
```

## Accepting Channels

```csharp
// Accept specific channel
var channel = await mux.AcceptChannelAsync("data");

// Accept with cancellation
var channel = await mux.AcceptChannelAsync("data", cancellationToken);

// Accept all channels (async enumerable)
await foreach (var channel in mux.AcceptChannelsAsync())
{
    Console.WriteLine($"New channel: {channel.ChannelId}");
    _ = HandleChannelAsync(channel);
}

// Accept with filter
await foreach (var channel in mux.AcceptChannelsAsync())
{
    if (channel.ChannelId.StartsWith("user/"))
        _ = HandleUserChannel(channel);
    else if (channel.ChannelId == "control")
        _ = HandleControlChannel(channel);
}
```

## Closing Channels

```csharp
// Graceful close (sends FIN frame)
await channel.DisposeAsync();

// Or use using statement
await using var channel = await mux.OpenChannelAsync(options);
// Channel disposed at end of scope
```

Closing a write channel:
1. Sends FIN frame
2. Remote reader sees EOF (0 bytes on read)
3. Resources released

## Channel States

```csharp
// Check state
var state = channel.State;

switch (state)
{
    case ChannelState.Open:
        // Normal operation
        break;
    case ChannelState.Closing:
        // FIN sent, waiting for ACK
        break;
    case ChannelState.Closed:
        // Fully closed
        break;
}
```

## Bidirectional Communication

For two-way communication, use two channels:

```csharp
// Manual approach
var sendToB = await muxA.OpenChannelAsync(new() { ChannelId = "a-to-b" });
var receiveFromB = await muxA.AcceptChannelAsync("b-to-a");

var sendToA = await muxB.OpenChannelAsync(new() { ChannelId = "b-to-a" });
var receiveFromA = await muxB.AcceptChannelAsync("a-to-b");
```

Or use [DuplexStreamTransit](../transits/duplex-stream.md):

```csharp
var duplexA = await muxA.OpenDuplexStreamAsync("chat");
var duplexB = await muxB.AcceptDuplexStreamAsync("chat");

// Both can read and write
await duplexA.WriteAsync(data);
await duplexB.ReadAsync(buffer);
```

## Channel Statistics

See [Statistics](../api/statistics.md) for full details.

```csharp
var stats = channel.Stats;

Console.WriteLine($"Bytes sent: {stats.BytesSent}");
Console.WriteLine($"Bytes received: {stats.BytesReceived}");
Console.WriteLine($"Credit starvation events: {stats.CreditStarvationCount}");
Console.WriteLine($"Waiting for credits: {stats.IsWaitingForCredits}");
```

## Channel Events

See [Events](events.md) for full details.

```csharp
var channel = await mux.OpenChannelAsync(options);

// Close event
channel.OnClosed += (reason, exception) =>
{
    Console.WriteLine($"Channel closed: {reason}");
    // ChannelCloseReason: LocalClose, RemoteFin, RemoteError, TransportFailed, MuxDisposed
};

// [Backpressure](backpressure.md) events
channel.OnCreditStarvation += () => Console.WriteLine("Blocked - waiting for credits");
channel.OnCreditRestored += (waitTime) => Console.WriteLine($"Credits restored after {waitTime}");
```

## Error Handling

```csharp
try
{
    await channel.WriteAsync(data);
}
catch (ChannelClosedException ex)
{
    Console.WriteLine($"Channel '{ex.ChannelId}' closed: {ex.CloseReason}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation cancelled");
}
catch (TimeoutException)
{
    Console.WriteLine("Send timeout - credits exhausted");
}
```

## Tips

**Use meaningful IDs:**
```csharp
// Good - descriptive
"user/123/messages"
"file-upload-abc123"
"control"

// Bad - unclear
"ch1"
"temp"
```

**Handle channel close:**
```csharp
var bytesRead = await channel.ReadAsync(buffer);
if (bytesRead == 0)
{
    // EOF - remote closed channel
}
```

**Dispose channels properly:**
```csharp
await using var channel = await mux.OpenChannelAsync(options);
// Automatically disposed
```
