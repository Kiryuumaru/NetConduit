# Graceful Shutdown

How to cleanly shut down a multiplexer and its channels.

## GoAway Protocol

`GoAwayAsync` signals the remote side that no new channels will be opened, then waits for existing channels to close:

```csharp
await mux.GoAwayAsync(cancellationToken);
```

The sequence:
1. Sends a GoAway frame to the remote side
2. Remote side receives `OnDisconnected` with `DisconnectReason.GoAwayReceived`
3. Both sides stop opening new channels
4. Waits for all existing channels to close (up to `GoAwayTimeout`)
5. Cancels the multiplexer run loop

```
Local                           Remote
  │                               │
  │──── GoAway frame ────────────>│
  │                               │ OnDisconnected(GoAwayReceived)
  │   (existing channels continue)│
  │<─── channel data ────────────>│
  │<─── channel FIN ──────────────│
  │──── channel FIN ─────────────>│
  │                               │
  │   (all channels closed)       │
  │──── shutdown ─────────────────│
```

### GoAwayTimeout

If channels don't close within the timeout, the multiplexer shuts down anyway:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    GoAwayTimeout = TimeSpan.FromSeconds(10) // Default: 30s
};
```

### Detecting GoAway

The remote side receives a disconnection event:

```csharp
mux.OnDisconnected += (reason, ex) =>
{
    if (reason == DisconnectReason.GoAwayReceived)
    {
        Console.WriteLine("Remote side is shutting down gracefully");
        // Finish current work, then dispose
    }
};
```

Check `IsShuttingDown` to see if a GoAway has been sent or received:

```csharp
if (mux.IsShuttingDown)
{
    // Don't open new channels
}
```

## DisposeAsync

`DisposeAsync` performs a best-effort graceful shutdown, then forcefully cleans up:

```csharp
await mux.DisposeAsync();
```

What happens:
1. If running and GoAway hasn't been sent, sends GoAway (with `GracefulShutdownTimeout`)
2. Aborts all remaining channels with `ChannelCloseReason.MuxDisposed`
3. Waits for channels to dispose (with `GracefulShutdownTimeout`)
4. Fires `OnDisconnected` with `DisconnectReason.LocalDispose`
5. Cancels the run loop and releases all resources

### GracefulShutdownTimeout

Controls how long `DisposeAsync` waits for the GoAway and channel cleanup:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    GracefulShutdownTimeout = TimeSpan.FromSeconds(3) // Default: 5s
};
```

## Patterns

### Clean Server Shutdown

```csharp
var cts = new CancellationTokenSource();

// Handle shutdown signal
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var mux = StreamMultiplexer.Create(serverOptions);
var runTask = mux.Start(cts.Token);
await mux.WaitForReadyAsync(cts.Token);

try
{
    await foreach (var channel in mux.AcceptChannelsAsync(cts.Token))
    {
        _ = HandleChannelAsync(channel);
    }
}
catch (OperationCanceledException) { }

// Graceful shutdown — wait for channels to finish
await mux.GoAwayAsync();
```

### Client with GoAway Handling

```csharp
await using var mux = StreamMultiplexer.Create(clientOptions);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

mux.OnDisconnected += (reason, _) =>
{
    if (reason == DisconnectReason.GoAwayReceived)
    {
        // Server is shutting down — stop sending new requests
        stopNewRequests = true;
    }
};

// ... use the multiplexer ...
```

### GoAway vs Dispose

| Method | Behavior |
|--------|----------|
| `GoAwayAsync` | Graceful. Sends GoAway, waits for channels, then stops |
| `DisposeAsync` | Best-effort graceful + forceful cleanup |

Use `GoAwayAsync` when you want to give the remote side time to finish.
Use `DisposeAsync` when you want to shut down immediately (it still tries GoAway briefly).

## Configuration Reference

| Option | Default | Description |
|--------|---------|-------------|
| `GoAwayTimeout` | 30 seconds | How long `GoAwayAsync` waits for channels to close |
| `GracefulShutdownTimeout` | 5 seconds | How long `DisposeAsync` waits for GoAway + cleanup |

## See Also

- [StreamMultiplexer](../api/stream-multiplexer.md) — Lifecycle and methods
- [MultiplexerOptions](../api/multiplexer-options.md) — Timeout configuration
- [Events](events.md) — Disconnection events
- [Reconnection](reconnection.md) — Auto-reconnect after disconnection
