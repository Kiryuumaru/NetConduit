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
    Console.WriteLine("Reconnected — channels restored, in-flight data replayed");
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

## Data Integrity

Every channel maintains an automatic replay buffer that mirrors sent data. On reconnect, both sides exchange byte-position markers and replay any data the remote side hasn't confirmed receiving.

```
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  Normal operation:                                           │
│  Sender ──── data ───▶ Receiver                              │
│         ◀── credits ──                                       │
│  Ring buffer records every byte sent.                        │
│  Credit grants trim confirmed data from the ring.            │
│                                                              │
│  Disconnect:                                                 │
│  Sender    ╳╳╳╳╳╳    Receiver                                │
│  Ring buffer still holds unacknowledged bytes.               │
│                                                              │
│  Reconnect:                                                  │
│  Sender ── RECONNECT(positions) ──▶ Receiver                 │
│         ◀── RECONNECT(positions) ──                          │
│  Each side reports per-channel BytesReceived.                │
│  Each side replays unacked data from its ring buffer.        │
│                                                              │
│  Result: zero data loss                                      │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

**What is preserved:**
- Data written to channels before disconnect (already in the Pipe)
- Data written to channels during the disconnect window (buffered in the Pipe)
- Data that was in-flight through the transport at disconnect time (replayed from the ring buffer)
- Channel state (channels remain open across reconnects)

### How the Replay Buffer Works

Each `WriteChannel` records every byte sent into a per-channel ring buffer. The ring buffer is sized to the channel's [`MaxCredits`](../api/channel-options.md) (default 4MB). This is safe because the credit-based [backpressure](backpressure.md) system guarantees the sender can never have more unacknowledged in-flight data than `MaxCredits` — the ring buffer can always hold everything that hasn't been confirmed.

The ring buffer is a fixed-size circular buffer. When it fills, new data overwrites the oldest data. Because the credit window bounds in-flight data to `MaxCredits`, and the ring is sized to `MaxCredits`, the ring always retains all unacknowledged data.

### Ring Buffer Sizing

The replay buffer is sized automatically from `MaxCredits`:

| MaxCredits | Replay Buffer | Use Case |
|------------|---------------|----------|
| 64KB | 64KB | Low-memory telemetry |
| 4MB (default) | 4MB | General purpose |
| 8MB | 8MB | High-throughput bulk transfer |

To change the replay buffer size, adjust `MaxCredits` in [ChannelOptions](../api/channel-options.md):

```csharp
var options = new ChannelOptions
{
    ChannelId = "bulk-data",
    MaxCredits = 8 * 1024 * 1024  // 8MB credit window + 8MB replay buffer
};
```

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
