# ReadChannel

A channel for receiving data from the remote side. Inherits from `Stream`, so it works with any API that reads from streams.

## Basic Usage

Accept a channel and read data:

```csharp
var channel = await mux.AcceptChannelAsync("data");

var buffer = new byte[4096];
int bytesRead = await channel.ReadAsync(buffer);
```

Read until the remote side closes (EOF):

```csharp
var channel = await mux.AcceptChannelAsync("data");
var buffer = new byte[4096];
int bytesRead;

while ((bytesRead = await channel.ReadAsync(buffer)) > 0)
{
    ProcessData(buffer.AsSpan(0, bytesRead));
}
```

## Stream Behavior

`ReadChannel` is a **read-only stream**. Attempting to write, seek, or get length throws `NotSupportedException`.

| Property | Value |
|----------|-------|
| `CanRead` | `true` (while open) |
| `CanWrite` | `false` |
| `CanSeek` | `false` |

### ReadAsync

Returns the number of bytes read, or `0` when the channel is closed (EOF):

```csharp
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
```

The synchronous `Read(byte[], int, int)` method is also available but blocks the calling thread.

### FlushAsync

No-op on ReadChannel. Credits are granted automatically as data is read — no manual flushing is needed.

### CloseAsync

Gracefully closes the read side:

```csharp
await channel.CloseAsync(cancellationToken);
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ChannelId` | `string` | The channel identifier |
| `State` | `ChannelState` | Current state: `Opening`, `Open`, `Closing`, `Closed` |
| `Priority` | `ChannelPriority` | Channel priority level |
| `CurrentWindowSize` | `uint` | Current receive window — bytes available before credits must be replenished |
| `CloseReason` | `ChannelCloseReason?` | Why the channel closed (`null` while open) |
| `CloseException` | `Exception?` | Exception that caused closure, if any |
| `Stats` | `ChannelStats` | Per-channel statistics — see [Statistics](statistics.md) |

### CurrentWindowSize

The receive window tracks how many bytes can be received before new credits need to be issued to the sender. This is managed automatically — the multiplexer grants credits as you read data.

```csharp
Console.WriteLine($"Window: {channel.CurrentWindowSize} bytes");
```

## Events

### OnClosed

Fires when the channel closes for any reason:

```csharp
channel.OnClosed += (reason, ex) =>
{
    Console.WriteLine($"Channel {channel.ChannelId} closed: {reason}");
    if (ex is not null)
        Console.WriteLine($"  Error: {ex.Message}");
};
```

Close reasons:

| Reason | Description |
|--------|-------------|
| `LocalClose` | You called `CloseAsync()` or `DisposeAsync()` |
| `RemoteFin` | Remote side closed the write channel |
| `RemoteError` | Remote side sent an error frame |
| `TransportFailed` | Underlying transport connection failed |
| `MuxDisposed` | The multiplexer was disposed |

## Credit-Based Flow Control

ReadChannel participates in [backpressure](../concepts/backpressure.md) by granting credits to the remote writer. When you read data from a ReadChannel, the multiplexer automatically replenishes credits so the sender can continue writing.

The window size is controlled by the channel options on the **writer's** side:

| Option | Default | Description |
|--------|---------|-------------|
| `MinCredits` | 64 KB | Minimum credits — replenished when usage drops below this |
| `MaxCredits` | 4 MB | Maximum credits — upper bound of the receive window |

## Patterns

### Accept and Process

```csharp
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    _ = Task.Run(async () =>
    {
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await channel.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await ProcessAsync(channel.ChannelId, buffer.AsMemory(0, bytesRead));
        }
    });
}
```

### Pipe to Another Stream

Since `ReadChannel` is a `Stream`, you can copy directly:

```csharp
var channel = await mux.AcceptChannelAsync("file-transfer");
await using var file = File.Create("received.dat");
await channel.CopyToAsync(file);
```

### Use with StreamReader

```csharp
var channel = await mux.AcceptChannelAsync("text");
using var reader = new StreamReader(channel, leaveOpen: true);

string? line;
while ((line = await reader.ReadLineAsync()) is not null)
{
    Console.WriteLine(line);
}
```

## Disposal

Disposing a `ReadChannel` closes it:

```csharp
await channel.DisposeAsync();
// or: channel.Dispose();
```

EOF (reading 0 bytes) is the normal way to detect the remote side closed. After EOF, dispose the channel to clean up resources.

## See Also

- [WriteChannel](write-channel.md) — The sending counterpart
- [Channels](../concepts/channels.md) — Channel concepts and patterns
- [Backpressure](../concepts/backpressure.md) — Credit-based flow control
- [Statistics](statistics.md) — Per-channel metrics
