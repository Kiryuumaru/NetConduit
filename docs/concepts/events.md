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
| `GoAwayReceived` | Remote sent GOAWAY (graceful shutdown) |
| `TransportError` | Underlying stream error (network failure) |
| `LocalDispose` | Local DisposeAsync() called |

### OnAutoReconnecting

Fired during auto-reconnection attempts. Provides `AutoReconnectEventArgs` with attempt details and ability to cancel. See [Reconnection](reconnection.md) for configuration.

```csharp
mux.OnAutoReconnecting += (args) =>
{
    Console.WriteLine($"Reconnect attempt {args.AttemptNumber}/{args.MaxAttempts}");
    Console.WriteLine($"Next delay: {args.NextDelay}");
    
    // Optionally cancel reconnection
    if (args.AttemptNumber > 5)
        args.Cancel = true;
};
```

**AutoReconnectEventArgs properties:**

| Property | Type | Description |
|----------|------|-------------|
| `AttemptNumber` | `int` | Current attempt (1-based) |
| `MaxAttempts` | `int` | Maximum configured attempts |
| `NextDelay` | `TimeSpan` | Delay before next attempt |
| `LastException` | `Exception?` | Exception from last attempt |
| `IsReconnecting` | `bool` | True if reconnecting, false if initial connection |
| `Cancel` | `bool` | Set to true to cancel reconnection |

### OnAutoReconnectFailed

Fired when auto-reconnection has permanently failed:

```csharp
mux.OnAutoReconnectFailed += (exception) =>
{
    Console.WriteLine($"Reconnection failed: {exception.Message}");
    // UI: Show "Connection lost" error
};
```

### OnChannelOpened

Fired when a channel is opened:

```csharp
mux.OnChannelOpened += (channelId) =>
{
    Console.WriteLine($"Channel opened: {channelId}");
};
```

### OnChannelClosed

Fired when a channel is closed:

```csharp
mux.OnChannelClosed += (channelId, exception) =>
{
    Console.WriteLine($"Channel closed: {channelId}");
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};
```

### OnError

Fired when an error occurs:

```csharp
mux.OnError += (exception) =>
{
    Console.WriteLine($"Error: {exception.Message}");
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

Fired when channel blocks waiting for credits (WriteChannel only). See [Backpressure](backpressure.md) for more details.

```csharp
channel.OnCreditStarvation += () =>
{
    Console.WriteLine($"Channel '{channel.ChannelId}' blocked - waiting for credits");
};
```

### OnCreditRestored

Fired when credits are received after starvation (WriteChannel only):

```csharp
channel.OnCreditRestored += (waitTime) =>
{
    Console.WriteLine($"Credits restored after {waitTime.TotalMilliseconds}ms");
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

// Check connection status via boolean properties
Console.WriteLine($"Connected: {mux.IsConnected}");
Console.WriteLine($"Running: {mux.IsRunning}");
Console.WriteLine($"Shutting down: {mux.IsShuttingDown}");
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
// States: Opening, Open, Closing, Closed
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

## Full Example

```csharp
var mux = StreamMultiplexer.Create(options);

// Wire up all events
mux.OnDisconnected += (reason, ex) =>
{
    logger.Warning($"Disconnected: {reason}");
    UpdateStatus("Disconnected");
};

mux.OnAutoReconnecting += (args) =>
{
    logger.Info($"Reconnecting (attempt {args.AttemptNumber})...");
    UpdateStatus("Reconnecting");
};

mux.OnAutoReconnectFailed += (ex) =>
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
