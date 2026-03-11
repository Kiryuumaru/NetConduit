# Events

Notifications for connection state changes and [channel](channels.md) lifecycle. See [Concepts Overview](index.md) for related topics.

## Multiplexer Events

### OnDisconnected

Fired when the multiplexer loses connection:

```csharp
mux.OnDisconnected += (reason, exception) =>
{
    Console.WriteLine($"Disconnected: {reason}");
    
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};
```

**DisconnectReason values:**

| Reason | Description |
|--------|-------------|
| `TransportError` | Underlying stream error (network failure) |
| `PingTimeout` | No pong response within timeout |
| `GoAwayReceived` | Remote sent GOAWAY (graceful shutdown) |
| `LocalDispose` | Local DisposeAsync() called |
| `ProtocolError` | Invalid frame received |

### OnReconnecting

Fired when attempting to reconnect. See [Reconnection](reconnection.md) for configuration.

```csharp
mux.OnReconnecting += () =>
{
    Console.WriteLine("Attempting to reconnect...");
    // UI: Show "Reconnecting" spinner
};
```

### OnReconnected

Fired when reconnection succeeds:

```csharp
mux.OnReconnected += () =>
{
    Console.WriteLine("Reconnected!");
    // UI: Show "Connected" status
};
```

### OnReconnectFailed

Fired when reconnection ultimately fails:

```csharp
mux.OnReconnectFailed += (exception) =>
{
    Console.WriteLine($"Reconnection failed: {exception.Message}");
    // UI: Show "Connection lost" error
};
```

## Channel Events

### OnClosed

Fired when a channel closes:

```csharp
channel.OnClosed += (reason, exception) =>
{
    Console.WriteLine($"Channel '{channel.ChannelId}' closed: {reason}");
    
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};
```

**ChannelCloseReason values:**

| Reason | Description |
|--------|-------------|
| `LocalClose` | Local DisposeAsync() called |
| `RemoteFin` | Remote sent FIN (graceful close) |
| `RemoteError` | Remote sent error frame |
| `TransportFailed` | Underlying transport failed |
| `MuxDisposed` | Multiplexer was disposed |

### OnCreditStarvation

Fired when channel blocks waiting for credits. See [Backpressure](backpressure.md) for more details.

```csharp
channel.OnCreditStarvation += () =>
{
    Console.WriteLine($"Channel '{channel.ChannelId}' blocked - waiting for credits");
    // Log backpressure event
};
```

### OnCreditRestored

Fired when credits are received after starvation:

```csharp
channel.OnCreditRestored += (waitTime) =>
{
    Console.WriteLine($"Credits restored after {waitTime.TotalMilliseconds}ms");
    // Log recovery
};
```

## Checking State Properties

### Multiplexer State

```csharp
// Check current disconnect reason (if disconnected)
if (mux.DisconnectReason.HasValue)
{
    Console.WriteLine($"Was disconnected due to: {mux.DisconnectReason}");
}

// Check current state
Console.WriteLine($"Mux state: {mux.State}");
// States: Created, Connecting, Connected, Reconnecting, Disconnected, Disposed
```

### Channel State

```csharp
// Check close reason (if closed)
if (channel.CloseReason.HasValue)
{
    Console.WriteLine($"Channel closed due to: {channel.CloseReason}");
}

// Check current state
Console.WriteLine($"Channel state: {channel.State}");
// States: Open, Closing, Closed
```

## Exception Handling

### ChannelClosedException

Thrown when writing to a closed channel:

```csharp
try
{
    await channel.WriteAsync(data);
}
catch (ChannelClosedException ex)
{
    Console.WriteLine($"Cannot write to channel '{ex.ChannelId}'");
    Console.WriteLine($"Reason: {ex.CloseReason}");
}
```

### Common Patterns

```csharp
try
{
    await channel.WriteAsync(data);
}
catch (ChannelClosedException ex) when (ex.CloseReason == ChannelCloseReason.RemoteFin)
{
    // Expected - remote closed gracefully
}
catch (ChannelClosedException ex) when (ex.CloseReason == ChannelCloseReason.TransportFailed)
{
    // Network issue - may want to retry
}
catch (OperationCanceledException)
{
    // Operation was cancelled
}
```

## Event Best Practices

### Avoid Blocking in Handlers

```csharp
// Bad - blocks event dispatch
mux.OnDisconnected += async (reason, ex) =>
{
    await SaveStateAsync();  // Don't await in event handler
};

// Good - fire and forget or queue
mux.OnDisconnected += (reason, ex) =>
{
    _ = SaveStateAsync();  // Fire and forget
    // Or: eventQueue.Enqueue(new DisconnectEvent(reason));
};
```

### Handle Exceptions in Handlers

```csharp
mux.OnDisconnected += (reason, ex) =>
{
    try
    {
        UpdateUI();
    }
    catch (Exception e)
    {
        logger.Error($"Handler error: {e.Message}");
    }
};
```

### Unsubscribe When Done

```csharp
EventHandler<(DisconnectReason, Exception?)> handler = (r, e) =>
{
    Console.WriteLine("Disconnected");
};

mux.OnDisconnected += handler;

// Later, when disposing
mux.OnDisconnected -= handler;
```

## Full Example

```csharp
var mux = StreamMultiplexer.Create(options);

// Wire up all events
mux.OnDisconnected += (reason, ex) =>
{
    logger.Warning($"Disconnected: {reason}");
    UpdateStatus("Disconnected");
};

mux.OnReconnecting += () =>
{
    logger.Info("Reconnecting...");
    UpdateStatus("Reconnecting");
};

mux.OnReconnected += () =>
{
    logger.Info("Reconnected");
    UpdateStatus("Connected");
};

mux.OnReconnectFailed += (ex) =>
{
    logger.Error($"Reconnection failed: {ex.Message}");
    UpdateStatus("Connection Failed");
    ShowReconnectButton();
};

var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Open channel with events
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

channel.OnClosed += (reason, ex) =>
{
    logger.Info($"Channel closed: {reason}");
};

channel.OnCreditStarvation += () =>
{
    logger.Debug("Backpressure detected");
};

channel.OnCreditRestored += (waitTime) =>
{
    if (waitTime > TimeSpan.FromSeconds(1))
        logger.Warning($"Long credit wait: {waitTime}");
};
```
