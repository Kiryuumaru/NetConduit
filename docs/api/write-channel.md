# WriteChannel

Outbound channel for writing data to the remote side. Inherits from `Stream`. See [Channels](../concepts/channels.md) for concepts.

## Creating

```csharp
// From multiplexer
var channel = mux.OpenChannel("data");

// With options
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High
});
```

## Writing Data

```csharp
// WriteAsync (primary method)
await channel.WriteAsync(data, cancellationToken);

// Stream compatibility
using var writer = new StreamWriter(channel, leaveOpen: true);
await writer.WriteLineAsync("Hello!");

// CopyToAsync
await sourceFile.CopyToAsync(channel);
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ChannelId` | `string` | The channel identifier |
| `State` | `ChannelState` | Current state: Opening, Open, Closing, Closed |
| `Priority` | `ChannelPriority` | Priority level |
| `Stats` | `ChannelStats` | Bytes/frames sent |
| `CloseReason` | `ChannelCloseReason?` | Why the channel was closed |
| `CloseException` | `Exception?` | Exception that caused closure |
| `CanRead` | `bool` | Always `false` |
| `CanWrite` | `bool` | `true` when state is Open or Opening |
| `CanSeek` | `bool` | Always `false` |

## Events

```csharp
channel.OnClosed += (reason, exception) =>
{
    Console.WriteLine($"Write channel closed: {reason}");
};
```

## Disposing

```csharp
// Explicit
await channel.DisposeAsync();

// await using
await using var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

Disposing a write channel sends a FIN to the remote side, signaling end-of-stream.
