# IPC Transport

Inter-Process Communication using TCP loopback (Windows) or Unix domain sockets (Linux/macOS). Fastest option for same-machine communication. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.Ipc
```

## Client

```csharp
using NetConduit;
using NetConduit.Ipc;

var options = IpcMultiplexer.CreateOptions("my-app-ipc");
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("rpc");
await channel.WriteAsync(requestData);
```

## Server

```csharp
using NetConduit;
using NetConduit.Ipc;

var options = IpcMultiplexer.CreateServerOptions("my-app-ipc");
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

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
    mux.Start();
    await mux.WaitForReadyAsync();

    _ = Task.Run(async () =>
    {
        await foreach (var channel in mux.AcceptChannelsAsync())
        {
            _ = HandleChannelAsync(channel);
        }
    });
}
```

## Endpoint Names

The endpoint string maps to platform-specific behavior:

| Platform | Endpoint `"my-app"` resolves to |
|----------|--------------------------------|
| Windows | TCP loopback on a deterministic port (SHA256 hash of name → port 49152–65535) |
| Linux/macOS | Unix domain socket at the endpoint path directly (e.g., `my-app`) |

## API

| Method | Signature | Description |
|--------|-----------|-------------|
| `CreateOptions` | `IpcMultiplexer.CreateOptions(string endpoint)` | Client options |
| `CreateServerOptions` | `IpcMultiplexer.CreateServerOptions(string endpoint)` | Server options |
