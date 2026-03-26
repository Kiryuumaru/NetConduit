# IPC Transport

Inter-Process Communication using named pipes (Windows) or Unix domain sockets (Linux/macOS). Fastest option for same-machine communication. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.Ipc
```

## Client

```csharp
using NetConduit;
using NetConduit.Ipc;

// Create client options with pipe/socket name
var options = IpcMultiplexer.CreateOptions("my-app-ipc");

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels
var channel = await mux.OpenChannelAsync(new() { ChannelId = "rpc" });
await channel.WriteAsync(requestData);
```

## Server

```csharp
using NetConduit;
using NetConduit.Ipc;

// Create server options
var options = IpcMultiplexer.CreateServerOptions("my-app-ipc");

// Create and start multiplexer  
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Accept channels
await foreach (var channel in mux.AcceptChannelsAsync())
{
    _ = HandleChannelAsync(channel);
}
```

## Multi-Client Server

For multiple concurrent IPC clients, create a multiplexer per connection:

```csharp
while (true)
{
    var options = IpcMultiplexer.CreateServerOptions("my-app-ipc");
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();

    // Handle this client's channels...
    await foreach (var channel in mux.AcceptChannelsAsync())
    {
        _ = HandleChannelAsync(channel);
    }
}
```

## Platform Behavior

| Platform | Implementation | Notes |
|----------|---------------|-------|
| Windows | TCP loopback | Deterministic port from endpoint name (SHA256 hash, range 49152–65535) |
| Linux | Unix Domain Socket | Socket at path derived from endpoint name |
| macOS | Unix Domain Socket | Socket at path derived from endpoint name |

The IPC transport automatically uses the correct mechanism for the current OS.

## Configuration

IPC transport uses the standard `configure` callback for multiplexer options:

```csharp
var options = IpcMultiplexer.CreateOptions("my-app-ipc", configure: o =>
{
    o.FlushMode = FlushMode.Immediate;
    o.EnableReconnection = false;
});
```

## Use Cases

**Process isolation with communication:**
```csharp
// Main app creates IPC server
var server = IpcMultiplexer.CreateServerOptions("myapp-worker");

// Worker process connects
var client = IpcMultiplexer.CreateOptions("myapp-worker");
```

**Plugin architecture:**
```csharp
// Host app
var hostOptions = IpcMultiplexer.CreateServerOptions("myapp-plugins");

// Each plugin connects via IPC
var pluginOptions = IpcMultiplexer.CreateOptions("myapp-plugins");
```

**Service communication:**
```csharp
// Systemd service or Windows service
var serviceOptions = IpcMultiplexer.CreateServerOptions("myservice");
```

## Tips

**Unique names per instance:**
```csharp
// Include PID or GUID for multiple instances
var pipeName = $"myapp-{Process.GetCurrentProcess().Id}";
```

**Clean up stale sockets (Linux):**
```csharp
var socketPath = "/tmp/my-app-ipc.sock";
if (File.Exists(socketPath))
    File.Delete(socketPath);
```

**Security considerations:**
- Windows: Use pipe security rules to restrict access
- Linux/macOS: Set appropriate file permissions on socket

## Performance

IPC is the fastest transport option:
- No network stack overhead
- Direct kernel-mediated communication
- Ideal for high-frequency local RPC

Typical throughput: 1-10 GB/s depending on system.

## When to Use IPC

| Scenario | Use IPC? |
|----------|----------|
| Same-machine services | ✅ Yes |
| Parent-child processes | ✅ Yes |
| Plugin systems | ✅ Yes |
| Cross-machine communication | ❌ No - use TCP/WebSocket |
| Container-to-host | ⚠️ Requires socket/pipe mounting |
