# Reconnection

Automatic recovery from network disconnects with [channel](channels.md) state restoration. See [Concepts Overview](index.md) for related topics.

## How It Works

Reconnection is always enabled. When the transport connection drops, the multiplexer automatically uses `StreamFactory` to create a new connection and reattach existing channels via session ID matching.

```
┌─────────────────────────────────────────────────────────────┐
│                                                             │
│  Normal Operation                                           │
│  ┌────────┐    Stream    ┌────────┐                         │
│  │ Client │◄────────────▶│ Server │                         │
│  └────────┘              └────────┘                         │
│                                                             │
│  Disconnect                                                 │
│  ┌────────┐    BROKEN    ┌────────┐                         │
│  │ Client │    ╳╳╳╳╳╳    │ Server │                         │
│  └────────┘              └────────┘                         │
│                                                             │
│  Reconnect (via StreamFactory)                              │
│  ┌────────┐  New Stream  ┌────────┐                         │
│  │ Client │◄────────────▶│ Server │                         │
│  │ Resume │              │ Resume │  ◀─ Channels reattached │
│  └────────┘              └────────┘                         │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## Reconnection Setup

Reconnection requires `StreamFactory`. Transport helpers set this automatically:

```csharp
using NetConduit;
using NetConduit.Tcp;

// StreamFactory is set by the transport helper — reconnection works automatically
var options = TcpMultiplexer.CreateOptions("localhost", 5000);

var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// On disconnect, multiplexer automatically reconnects
```

## Reconnection Events

See [Events](events.md) for full event details.

```csharp
var mux = StreamMultiplexer.Create(options);

mux.OnDisconnected += (reason, exception) =>
{
    Console.WriteLine($"Disconnected: {reason}");
    // DisconnectReason: GoAwayReceived, TransportError, LocalDispose
    
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};

mux.OnAutoReconnecting += (args) =>
{
    Console.WriteLine($"Reconnect attempt {args.AttemptNumber}/{args.MaxAttempts}");
    Console.WriteLine($"Next delay: {args.NextDelay}");
    
    // Cancel if needed
    if (args.AttemptNumber > 10)
        args.Cancel = true;
};

mux.OnAutoReconnectFailed += (exception) =>
{
    Console.WriteLine($"Reconnection failed: {exception.Message}");
    // Maximum attempts exceeded
};

mux.OnReconnected += () =>
{
    Console.WriteLine("Reconnected — channels restored, buffered data delivered");
};
```

## Configuration Options

See [MultiplexerOptions](../api/multiplexer-options.md) for full configuration.

| Option | Default | Description |
|--------|---------|-------------|
| `MaxAutoReconnectAttempts` | 0 (unlimited) | Max reconnection attempts before giving up |
| `AutoReconnectDelay` | 1s | Initial delay between reconnect attempts |
| `MaxAutoReconnectDelay` | 30s | Maximum delay (exponential backoff cap) |
| `AutoReconnectBackoffMultiplier` | 2.0 | Multiplier for exponential backoff |

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000) with
{
    MaxAutoReconnectAttempts = 10,
    AutoReconnectDelay = TimeSpan.FromSeconds(2),
    MaxAutoReconnectDelay = TimeSpan.FromSeconds(30)
};
```

## Data Preservation

The multiplexer's internal write buffer (Pipe) persists across reconnections. Data written during the disconnect window is buffered in memory and drained to the new stream after reconnect completes.

```
Write("A") → Write("B") → [disconnect] → Write("C") → [reconnect] → A, B, C all delivered
```

**What is preserved:**
- Data written to channels before disconnect (already in the Pipe)
- Data written to channels during the disconnect window (buffered in the Pipe)
- Channel state (channels remain open across reconnects)

**What is lost:**
- Data that was in-flight through the transport at disconnect time (already handed to the OS/network, not yet received by the remote)

This is analogous to real network behavior: data inside your application is safe, data on the wire may be lost.

## Channel Behavior During Reconnection

### Write Operations

```csharp
// Writes succeed during disconnect — data is buffered and delivered after reconnect
await channel.WriteAsync(data);
```

### Read Operations

```csharp
// Reads block until data arrives (after reconnection)
var n = await channel.ReadAsync(buffer);

// Or use cancellation
var n = await channel.ReadAsync(buffer, cancellationToken);
```

### Channel State

Channels remain open during reconnection:

```csharp
// Channel is still valid
Console.WriteLine(channel.State);  // Still "Open" during reconnection

// Check multiplexer connection status
Console.WriteLine($"Connected: {mux.IsConnected}");
Console.WriteLine($"Reconnecting: {mux.IsReconnecting}");
Console.WriteLine($"Running: {mux.IsRunning}");
```

## Reconnection Strategies

### Quick Reconnect (Default)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000) with
{
    AutoReconnectDelay = TimeSpan.FromSeconds(1)
};
// Fast reconnection for transient network issues
```

### Persistent Connection

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000) with
{
    AutoReconnectDelay = TimeSpan.FromSeconds(5),
    MaxAutoReconnectDelay = TimeSpan.FromMinutes(1)
};
// Long reconnection window for mobile/unstable networks
```

### Limited Attempts

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000) with
{
    MaxAutoReconnectAttempts = 3
};
// Give up after 3 reconnection attempts
```

## Handling Reconnection Failure

```csharp
mux.OnAutoReconnectFailed += async (exception) =>
{
    Console.WriteLine("Reconnection failed permanently");
    
    // Option 1: Give up
    await mux.DisposeAsync();
    
    // Option 2: Create new multiplexer
    var newMux = StreamMultiplexer.Create(options);
    await newMux.Start();
    
    // Channels from old mux are invalid
};
```

## Session Resumption

```csharp
// Multiplexer assigns session ID (Guid)
var sessionId = mux.SessionId;

// On reconnect, server validates session
// Channels are restored via session matching
```

## Server-Side Reconnection

Transport factory `CreateServerOptions` methods do not support reconnection (they throw on second call).

For TCP servers, use a custom `StreamFactory` that re-accepts from the listener:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async ct =>
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        client.NoDelay = true;
        return new StreamPair(client.GetStream(), client);
    },
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    MaxAutoReconnectAttempts = 5
};
```

For WebSocket servers, use `WebSocketMuxListener` which manages session routing automatically. See [WebSocket Transport](../transports/websocket.md) for details.

## Tips

**Handle both events:**
```csharp
mux.OnDisconnected += (r, e) =>
{
    // Update UI: "Reconnecting..."
};

mux.OnAutoReconnecting += (args) =>
{
    // Update UI: "Attempt {args.AttemptNumber}..."
};

mux.OnAutoReconnectFailed += (e) =>
{
    // Update UI: "Connection lost"
    // Prompt user to retry
};
```

**Graceful degradation:**
```csharp
// Not all operations should wait for reconnection
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await channel.WriteAsync(data, cts.Token);
}
catch (OperationCanceledException)
{
    // Queue locally or skip non-critical data
}
```
