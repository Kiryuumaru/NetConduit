# StreamMultiplexer

The core class that multiplexes multiple channels over a single bidirectional stream. Implements `IStreamMultiplexer` and `IAsyncDisposable`.

## Creating a Multiplexer

Use the static `Create` factory method with [MultiplexerOptions](multiplexer-options.md):

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000);
var mux = StreamMultiplexer.Create(options);
```

Transport helpers create the options for you — see [Transports](../transports/index.md).

## Lifecycle

A multiplexer goes through these stages:

```
Create → Start → Ready → Active → Shutdown/Dispose
```

### Start

`Start` launches the background run loop and returns a `Task` that completes when the multiplexer shuts down:

```csharp
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start(cancellationToken);

// runTask completes when the mux stops (disconnect, GoAway, dispose, or failure)
```

The returned task does **not** mean the connection is ready — use `WaitForReadyAsync` for that.

### WaitForReadyAsync

Blocks until the first successful connection and handshake complete:

```csharp
await mux.WaitForReadyAsync(cancellationToken);
// Now safe to open/accept channels
```

### GoAwayAsync

Initiates graceful shutdown — signals the remote side that no new channels will be opened:

```csharp
await mux.GoAwayAsync(cancellationToken);
```

See [Graceful Shutdown](../concepts/graceful-shutdown.md) for the full shutdown protocol.

### DisposeAsync

Disposes all channels and the underlying transport:

```csharp
await mux.DisposeAsync();
```

Always dispose in a `finally` block or use `await using`:

```csharp
await using var mux = StreamMultiplexer.Create(options);
```

## Channel Operations

### Opening Channels

Open an outbound channel (the remote side must accept it):

```csharp
// Simple — default options
var channel = await mux.OpenChannelAsync("data", cancellationToken);

// Custom options
var channel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High,
    MinCredits = 128 * 1024,
    MaxCredits = 8 * 1024 * 1024
}, cancellationToken);
```

### Accepting Channels

Accept a specific channel by ID:

```csharp
var channel = await mux.AcceptChannelAsync("data", cancellationToken);
```

Or enumerate all incoming channels:

```csharp
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    _ = HandleChannelAsync(channel);
}
```

### Looking Up Channels

Retrieve active channels by ID:

```csharp
WriteChannel? write = mux.GetWriteChannel("data");
ReadChannel? read = mux.GetReadChannel("data");
```

Returns `null` if the channel doesn't exist or is closed.

## Properties

### Connection State

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | Whether the transport is currently connected |
| `IsRunning` | `bool` | Whether the background run loop is active |
| `IsShuttingDown` | `bool` | Whether a GoAway has been sent or received |
| `IsReconnecting` | `bool` | Whether a reconnection attempt is in progress |
| `CurrentConnectionAttempt` | `int` | Current reconnection attempt number (0 when connected) |
| `DisconnectReason` | `DisconnectReason?` | Why the multiplexer disconnected (`null` if connected) |
| `DisconnectException` | `Exception?` | The exception that caused disconnection, if any |

### Session Identity

| Property | Type | Description |
|----------|------|-------------|
| `SessionId` | `Guid` | Local session identifier (set at creation or via options) |
| `RemoteSessionId` | `Guid` | Remote session identifier (available after handshake) |

Session IDs are exchanged during the handshake and used for reconnection routing.

### Channel Information

| Property | Type | Description |
|----------|------|-------------|
| `ActiveChannelIds` | `IReadOnlyCollection<string>` | IDs of all active channels (opened + accepted) |
| `OpenedChannelIds` | `IReadOnlyCollection<string>` | IDs of channels opened by this side |
| `AcceptedChannelIds` | `IReadOnlyCollection<string>` | IDs of channels accepted from the remote side |
| `ActiveChannelCount` | `int` | Count of active channels |

### Other

| Property | Type | Description |
|----------|------|-------------|
| `Options` | `MultiplexerOptions` | The configuration this multiplexer was created with |
| `Stats` | `MultiplexerStats` | Runtime statistics — see [Statistics](statistics.md) |

## Events

### Connection Events

```csharp
// Fires when multiplexer is ready (connected + handshake complete)
mux.OnReady += () => Console.WriteLine("Ready");

// Fires on disconnection
mux.OnDisconnected += (reason, ex) =>
    Console.WriteLine($"Disconnected: {reason}, {ex?.Message}");

// Fires on successful reconnection
mux.OnReconnected += () => Console.WriteLine("Reconnected");
```

### Reconnection Events

```csharp
// Fires before each reconnection attempt
mux.OnAutoReconnecting += args =>
{
    Console.WriteLine($"Reconnecting: attempt {args.AttemptNumber}/{args.MaxAttempts}");
    // args.Cancel = true; // to abort reconnection
};

// Fires when all reconnection attempts are exhausted
mux.OnAutoReconnectFailed += ex =>
    Console.WriteLine($"Reconnection failed permanently: {ex.Message}");
```

### Channel Events

```csharp
// Fires when any channel opens
mux.OnChannelOpened += channelId =>
    Console.WriteLine($"Channel opened: {channelId}");

// Fires when any channel closes
mux.OnChannelClosed += (channelId, ex) =>
    Console.WriteLine($"Channel closed: {channelId}, error: {ex?.Message}");
```

### Error Events

```csharp
// Fires on non-fatal errors (protocol violations, frame errors)
mux.OnError += ex => Console.WriteLine($"Error: {ex.Message}");
```

See [Events](../concepts/events.md) for patterns and best practices.

## Advanced Methods

### Flush

Forces an immediate flush of all buffered frames:

```csharp
mux.Flush();
```

Relevant when using `FlushMode.Manual` — see [Flush Modes](../concepts/flush-modes.md).

### ReconnectAsync

Manually reconnect with new streams (used by transport helpers internally):

```csharp
await mux.ReconnectAsync(newReadStream, newWriteStream, cancellationToken);
```

This is called automatically when auto-reconnection is enabled. You only need this for custom transport implementations.

### NotifyDisconnected

Signals the multiplexer that the transport has disconnected:

```csharp
mux.NotifyDisconnected();
```

Used by transport implementations to trigger reconnection. Not needed in application code.

## Complete Example

```csharp
// Server
var serverOptions = TcpMultiplexer.CreateServerOptions(listener);
await using var server = StreamMultiplexer.Create(serverOptions);

server.OnChannelOpened += id => Console.WriteLine($"Channel: {id}");
server.OnDisconnected += (reason, _) => Console.WriteLine($"Disconnected: {reason}");

var serverTask = server.Start();
await server.WaitForReadyAsync();

await foreach (var channel in server.AcceptChannelsAsync())
{
    var buffer = new byte[4096];
    var read = await channel.ReadAsync(buffer);
    Console.WriteLine($"[{channel.ChannelId}] {Encoding.UTF8.GetString(buffer, 0, read)}");
}

// Client
var clientOptions = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    MaxAutoReconnectAttempts = 5
};
await using var client = StreamMultiplexer.Create(clientOptions);

var clientTask = client.Start();
await client.WaitForReadyAsync();

var channel = await client.OpenChannelAsync("hello");
await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, NetConduit!"));
await channel.CloseAsync();
```

## See Also

- [MultiplexerOptions](multiplexer-options.md) — Configuration
- [Statistics](statistics.md) — Runtime metrics
- [Events](../concepts/events.md) — Event handling patterns
- [Graceful Shutdown](../concepts/graceful-shutdown.md) — GoAway protocol
- [Reconnection](../concepts/reconnection.md) — Auto-reconnect behavior
