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

For multiple concurrent IPC clients:

```csharp
var server = new IpcServer("my-app-ipc");

server.OnClientConnected += async (clientStream) =>
{
    var options = new MultiplexerOptions
    {
        StreamFactory = async (ct) => (clientStream, clientStream)
    };
    
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    
    // Handle this client's channels...
};

await server.StartAsync();
```

## Platform Behavior

| Platform | Implementation | Path |
|----------|---------------|------|
| Windows | Named Pipes | `\\.\pipe\my-app-ipc` |
| Linux | Unix Domain Socket | `/tmp/my-app-ipc.sock` |
| macOS | Unix Domain Socket | `/tmp/my-app-ipc.sock` |

The IPC transport automatically uses the correct mechanism for the current OS.

## Configuration

### Pipe Settings (Windows)

```csharp
var options = IpcMultiplexer.CreateServerOptions("my-app-ipc");

// Configure pipe security
options.ConfigurePipeSecurity = (security) =>
{
    // Allow all users
    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        PipeAccessRights.ReadWrite,
        AccessControlType.Allow));
};
```

### Socket Permissions (Linux/macOS)

```csharp
var options = IpcMultiplexer.CreateServerOptions("my-app-ipc");

// Socket file permissions (octal)
options.UnixSocketPermissions = 0x1FF;  // 0777 - all users
```

### Custom Path

```csharp
// Specify custom socket/pipe path
var options = IpcMultiplexer.CreateOptions("/var/run/myapp/socket");
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
