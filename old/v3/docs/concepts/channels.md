# Channels

Channels are virtual one-way streams over a single physical connection. See [Concepts Overview](index.md) for related concepts.

## Simplex Design

Channels are **simplex** (one-way):

| Channel Type    | Created By             | Direction    |
| --------------- | ---------------------- | ------------ |
| `IWriteChannel` | `OpenChannel()`        | You → Remote |
| `IReadChannel`  | `AcceptChannelAsync()` | Remote → You |

```csharp
// Side A opens - gets WriteChannel
var send = mux.OpenChannel("data");

// Side B accepts - gets ReadChannel
var receive = await mux.AcceptChannelAsync("data");

// A writes, B reads
await send.WriteAsync(data);
var n = await receive.ReadAsync(buffer);
```

## Channel IDs

Channel IDs are strings:

```csharp
// Simple names
var ch1 = mux.OpenChannel("control");
var ch2 = mux.OpenChannel("data");

// Structured names
var ch3 = mux.OpenChannel("user/123/messages");
var ch4 = mux.OpenChannel($"file-{Guid.NewGuid()}");
```

Each channel ID must be unique per direction:
- You can have `OpenChannel("data")` and `AcceptChannelAsync("data")` (different directions)
- You cannot have two `OpenChannel("data")` (same direction, same ID)

## Stream Interop

Channels provide an `AsStream()` method for APIs that require a `Stream`:

```csharp
var channel = mux.OpenChannel("text");
var stream = channel.AsStream();

// Use with StreamWriter
using var writer = new StreamWriter(stream, leaveOpen: true);
await writer.WriteLineAsync("Hello!");

// Use with CopyToAsync
await sourceStream.CopyToAsync(stream);
```

## Opening Channels

```csharp
// Basic open
var channel = mux.OpenChannel("data");

// With options
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High,
    SlabSize = 4 * 1024 * 1024,          // 4MB slab
    SendTimeout = TimeSpan.FromSeconds(30)
});
```

## Accepting Channels

```csharp
// Accept specific channel by ID
var channel = await mux.AcceptChannelAsync("data", cancellationToken);

// Accept all channels as they arrive
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    Console.WriteLine($"Got channel: {channel.ChannelId}");
    _ = HandleChannelAsync(channel);
}
```

## Channel Properties

| Property         | Type                  | Description                                                 |
| ---------------- | --------------------- | ----------------------------------------------------------- |
| `ChannelId`      | `string`              | The channel identifier                                      |
| `State`          | `ChannelState`        | Current state: Opening, Open, Closing, Closed               |
| `IsReady`        | `bool`                | Whether channel is confirmed by remote (stays true forever) |
| `IsConnected`    | `bool`                | Whether the underlying transport is active                  |
| `Priority`       | `ChannelPriority`     | Priority level                                              |
| `Stats`          | `ChannelStats`        | Bytes/frames sent and received                              |
| `CloseReason`    | `ChannelCloseReason?` | Why the channel was closed                                  |
| `CloseException` | `Exception?`          | Exception that caused closure (if any)                      |

## Channel Stats

```csharp
var stats = channel.Stats;
Console.WriteLine($"Sent: {stats.BytesSent} bytes ({stats.FramesSent} frames)");
Console.WriteLine($"Received: {stats.BytesReceived} bytes ({stats.FramesReceived} frames)");
```

## Channel Lifecycle

```
Opening → Open → Closing → Closed
```

- **Opening** — Channel negotiation in progress
- **Open** — Ready for read/write
- **Closing** — Close initiated, draining
- **Closed** — Fully closed

## Close Event

```csharp
channel.Closed += (sender, e) =>
{
    Console.WriteLine($"Channel closed: {e.Reason}");
    if (e.Exception is not null)
        Console.WriteLine($"Error: {e.Exception.Message}");
};
```

## Close Reasons

| Reason            | Description                       |
| ----------------- | --------------------------------- |
| `LocalClose`      | You disposed the channel          |
| `RemoteFin`       | Remote side closed gracefully     |
| `RemoteError`     | Remote side sent an error         |
| `TransportFailed` | Underlying transport disconnected |
| `MuxDisposed`     | Multiplexer was disposed          |

## Disposing Channels

```csharp
// Explicit dispose
await channel.DisposeAsync();

// Or use await using
await using var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
// Automatically closed at end of scope
```
