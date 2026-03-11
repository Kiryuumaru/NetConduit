# Reconnection

Automatic recovery from network disconnects with channel state restoration.

## How It Works

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
│  │ Buffer │              │ Buffer │  ◀─ Pending data held   │
│  └────────┘              └────────┘                         │
│                                                             │
│  Reconnect (via StreamFactory)                              │
│  ┌────────┐  New Stream  ┌────────┐                         │
│  │ Client │◄────────────▶│ Server │                         │
│  │ Flush  │─────────────▶│ Apply  │  ◀─ Buffered data sent  │
│  └────────┘              └────────┘                         │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## Enabling Reconnection

Reconnection requires `StreamFactory`:

```csharp
using NetConduit;
using NetConduit.Tcp;

// StreamFactory enables reconnection - multiplexer calls it 
// to establish new connections when needed
var options = TcpMultiplexer.CreateOptions("localhost", 5000);
options.EnableReconnection = true;
options.ReconnectTimeout = TimeSpan.FromSeconds(60);
options.ReconnectBufferSize = 1024 * 1024;  // 1MB buffer

var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// On disconnect, multiplexer automatically reconnects
```

## Server Setup

Server must also support reconnection:

```csharp
var options = TcpMultiplexer.CreateServerOptions(listener);
options.EnableReconnection = true;
options.ReconnectTimeout = TimeSpan.FromSeconds(60);

var mux = StreamMultiplexer.Create(options);
// Server accepts reconnecting clients transparently
```

## Reconnection Events

```csharp
var mux = StreamMultiplexer.Create(options);

mux.OnDisconnected += (reason, exception) =>
{
    Console.WriteLine($"Disconnected: {reason}");
    // DisconnectReason: TransportError, PingTimeout, GoAwayReceived, etc.
    
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};

mux.OnReconnecting += () =>
{
    Console.WriteLine("Attempting to reconnect...");
};

mux.OnReconnected += () =>
{
    Console.WriteLine("Reconnected successfully!");
    // Channels are still valid, buffered data will be sent
};

mux.OnReconnectFailed += (exception) =>
{
    Console.WriteLine($"Reconnection failed: {exception.Message}");
    // Maximum attempts exceeded or timeout reached
};
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `EnableReconnection` | true | Enable automatic reconnection |
| `ReconnectTimeout` | 60s | Maximum time to attempt reconnection |
| `ReconnectBufferSize` | 1MB | Buffer for pending data during disconnect |
| `ReconnectDelay` | 1s | Delay between reconnect attempts |
| `MaxReconnectAttempts` | unlimited | Max reconnection attempts (in timeout window) |

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000);
options.EnableReconnection = true;
options.ReconnectTimeout = TimeSpan.FromMinutes(2);
options.ReconnectBufferSize = 4 * 1024 * 1024;  // 4MB
options.ReconnectDelay = TimeSpan.FromSeconds(2);
```

## Channel Behavior During Reconnection

### Write Operations

```csharp
// During disconnect, writes are buffered (up to ReconnectBufferSize)
await channel.WriteAsync(data);  // May block until reconnected or timeout

// If buffer fills, oldest data may be dropped (for unreliable channels)
// or write blocks until space available
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

// Check multiplexer state for connection status
Console.WriteLine(mux.State);  // "Reconnecting" or "Connected"
```

## Reconnection Strategies

### Quick Reconnect (Default)

```csharp
options.ReconnectTimeout = TimeSpan.FromSeconds(30);
options.ReconnectDelay = TimeSpan.FromSeconds(1);
// Fast reconnection for transient network issues
```

### Persistent Connection

```csharp
options.ReconnectTimeout = TimeSpan.FromMinutes(10);
options.ReconnectDelay = TimeSpan.FromSeconds(5);
options.ReconnectBufferSize = 10 * 1024 * 1024;  // 10MB
// Long reconnection window for mobile/unstable networks
```

### No Reconnection

```csharp
options.EnableReconnection = false;
// Disconnect = game over
// Application handles reconnection manually
```

## Handling Reconnection Failure

```csharp
mux.OnReconnectFailed += async (exception) =>
{
    Console.WriteLine("Reconnection failed permanently");
    
    // Option 1: Give up
    await mux.DisposeAsync();
    
    // Option 2: Create new multiplexer
    var newMux = StreamMultiplexer.Create(options);
    await newMux.Start();
    
    // Note: Channels from old mux are invalid
};
```

## Session Resumption

For full session restoration:

```csharp
// Multiplexer assigns session ID
var sessionId = mux.SessionId;

// On reconnect, server validates session
// Channels are restored with pending data
```

## Tips

**Size buffer appropriately:**
```csharp
// Buffer should hold pending data during typical disconnect
// Too small = data loss during long disconnects
// Too large = memory pressure

// For interactive: 256KB - 1MB
// For bulk transfer: 4MB - 16MB
```

**Handle both events:**
```csharp
mux.OnDisconnected += (r, e) =>
{
    // Update UI: "Reconnecting..."
};

mux.OnReconnected += () =>
{
    // Update UI: "Connected"
};

mux.OnReconnectFailed += (e) =>
{
    // Update UI: "Connection lost"
    // Prompt user to retry
};
```

**Test reconnection:**
```csharp
// Simulate disconnect for testing
await mux.SimulateDisconnectAsync();  // If available

// Or physically disconnect network
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
