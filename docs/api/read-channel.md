# ReadChannel

Inbound channel for reading data from the remote side. Inherits from `Stream`. See [Channels](../concepts/channels.md) for concepts.

## Accepting

```csharp
// Accept specific channel by ID
var channel = await mux.AcceptChannelAsync("data", cancellationToken);

// Accept all channels
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    Console.WriteLine($"Got: {channel.ChannelId}");
}
```

## Reading Data

```csharp
// ReadAsync (primary method)
var buffer = new byte[4096];
var bytesRead = await channel.ReadAsync(buffer, cancellationToken);
// Returns 0 when the remote side closes the channel

// Stream compatibility
using var reader = new StreamReader(channel, leaveOpen: true);
var line = await reader.ReadLineAsync();

// CopyToAsync
await channel.CopyToAsync(destinationFile);
```

## Read Loop Pattern

```csharp
var buffer = new byte[4096];
int bytesRead;
while ((bytesRead = await channel.ReadAsync(buffer, ct)) > 0)
{
    ProcessData(buffer.AsSpan(0, bytesRead));
}
// Channel is closed when ReadAsync returns 0
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ChannelId` | `string` | The channel identifier |
| `State` | `ChannelState` | Current state: Opening, Open, Closing, Closed |
| `Priority` | `ChannelPriority` | Priority level |
| `Stats` | `ChannelStats` | Bytes/frames received |
| `CloseReason` | `ChannelCloseReason?` | Why the channel was closed |
| `CloseException` | `Exception?` | Exception that caused closure |
| `CanRead` | `bool` | `true` when state is Open or Opening |
| `CanWrite` | `bool` | Always `false` |
| `CanSeek` | `bool` | Always `false` |

## Events

```csharp
channel.OnClosed += (reason, exception) =>
{
    Console.WriteLine($"Read channel closed: {reason}");
};
```

## Disposing

```csharp
await channel.DisposeAsync();
```

Disposing a read channel signals the remote side that you're no longer interested in the data.
