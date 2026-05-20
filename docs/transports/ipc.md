# IPC transport

Package: [`NetConduit.Transport.Ipc`](https://www.nuget.org/packages/NetConduit.Transport.Ipc).

Same-machine inter-process communication. The implementation chooses the best mechanism per platform:

| Platform | Underlying mechanism |
| --- | --- |
| Windows | TCP loopback (`127.0.0.1`) on a deterministic port derived from the endpoint name. |
| Linux / macOS | Unix domain socket. The endpoint name is the socket file path. |

## API

```csharp
public static class IpcMultiplexer
{
    public static MultiplexerOptions CreateOptions(string endpoint);
    public static MultiplexerOptions CreateServerOptions(string endpoint);
}
```

| Helper | Behavior |
| --- | --- |
| `CreateOptions(endpoint)` | Client. Each factory call connects fresh. Reconnect-friendly. |
| `CreateServerOptions(endpoint)` | Server. Binds and accepts one connection. On Unix, removes any stale socket file at the path; cleans up on dispose. |

## Endpoint names

- Pass any UTF-8 string. The same string must be used by client and server.
- On Unix, the string is **treated as a file path** — pass a path you have permission to create at (e.g. `/tmp/my-app.sock` or `Path.Combine(Path.GetTempPath(), "my-app.sock")`).
- On Windows, the string is **hashed (SHA-256)** to derive a port in the ephemeral range `49152..65535`. The same name always maps to the same port. No file is created.

## Example

```csharp
using NetConduit;
using NetConduit.Transport.Ipc;

// Server
await using var server = StreamMultiplexer.Create(IpcMultiplexer.CreateServerOptions("my-app"));
server.Start();
await server.WaitForReadyAsync();

// Client (other process)
await using var client = StreamMultiplexer.Create(IpcMultiplexer.CreateOptions("my-app"));
client.Start();
await client.WaitForReadyAsync();
```

## Why use it

- **Latency.** No network stack.
- **No network exposure.** Useful for daemons that only need to talk to a CLI on the same box.
- **Cross-platform path.** One package, three operating systems.

## Reconnectable server

The platform-specific bind/cleanup logic inside `CreateServerOptions` is not exposed publicly. For a server that survives client churn, dispose the multiplexer on `Disconnected` and create a new one rather than driving it from a custom `StreamFactory`. See [Reconnection → IPC](../concepts/reconnection.md#ipc) for the recommended pattern.

## Caveats

- On Windows, two processes with the same endpoint name compete for the same loopback port. Pick distinct names per logical service.
- On Unix, the socket file persists if the server crashes uncleanly. The next `CreateServerOptions` call removes it before binding.
- Only same-machine traffic. For cross-host use TCP/WebSocket/QUIC.
