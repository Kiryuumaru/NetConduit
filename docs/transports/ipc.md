# IPC transport

Package: [`NetConduit.Transport.Ipc`](https://www.nuget.org/packages/NetConduit.Transport.Ipc).

Same-machine inter-process communication. The implementation chooses the best mechanism per platform:

| Platform | Underlying mechanism |
| --- | --- |
| Windows | TCP loopback (`127.0.0.1`) on an OS-assigned ephemeral port discovered through an endpoint registry file. |
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
| `CreateOptions(endpoint)` | Client. Each factory call connects fresh. On Windows, reads the endpoint registry file to discover the active loopback port. Reconnect-friendly. |
| `CreateServerOptions(endpoint)` | Server. Binds and accepts one connection. On Windows, publishes the selected loopback port in the endpoint registry file. On Unix, removes any stale socket file at the path; cleans up on dispose. |

## Endpoint names

- Pass any UTF-8 string. The same string must be used by client and server.
- On Unix, the string is **treated as a file path** — pass a path you have permission to create at (e.g. `/tmp/my-app.sock` or `Path.Combine(Path.GetTempPath(), "my-app.sock")`).
- On Windows, the server binds loopback to an **OS-assigned ephemeral port** and writes that selected port to `%LOCALAPPDATA%\NetConduit\ipc-endpoints\{SHA256(endpoint)}.port`.
- The Windows registry file is per-user and endpoint-keyed. It is deleted when the server-side stream pair is disposed.
- Windows clients read the registry file before each connection attempt. The endpoint name is stable, but the TCP port is not stable across server runs.

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

- On Windows, two server processes with the same endpoint name compete for the same endpoint registry file. Pick distinct names per logical service.
- On Windows, clients fail with connection-refused semantics when the registry file is missing because no server is currently publishing that endpoint.
- On Unix, the socket file persists if the server crashes uncleanly. The next `CreateServerOptions` call removes it before binding.
- Only same-machine traffic. For cross-host use TCP/WebSocket/QUIC.
