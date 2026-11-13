# Packages

NetConduit is shipped as one core package plus optional transports and transits. Each non-core package depends on the core `NetConduit` package, so installing a transport or transit automatically pulls in the core.

All packages target **net8.0**, **net9.0**, and **net10.0** and are AOT-compatible.

## Core

| Package | Contains |
| --- | --- |
| [`NetConduit`](https://www.nuget.org/packages/NetConduit) | `StreamMultiplexer`, `StreamPair`, channel interfaces, options, framing, flow control, keepalive, reconnection |

## Multi-hop routing

| Package | Contains |
| --- | --- |
| [`NetConduit.Mesh`](https://www.nuget.org/packages/NetConduit.Mesh) | `MeshMultiplexer` — routed overlay on top of neighbour `StreamMultiplexer` instances |

## Transits

Transits are optional layers that wrap one or two channels and turn raw bytes into a higher-level abstraction (a `Stream`, a typed message queue, or a delta-synced state).

| Package | Wraps | Provides |
| --- | --- | --- |
| [`NetConduit.Transit.Stream`](https://www.nuget.org/packages/NetConduit.Transit.Stream) | One channel (read **or** write) | A simplex `System.IO.Stream` |
| [`NetConduit.Transit.DuplexStream`](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream) | One write + one read channel | A bidirectional `System.IO.Stream` |
| [`NetConduit.Transit.Message`](https://www.nuget.org/packages/NetConduit.Transit.Message) | One channel pair | Length-prefixed typed JSON messages |
| [`NetConduit.Transit.DeltaMessage`](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage) | One channel pair | State sync via JSON deltas |

You can use multiple transits on a single multiplexer — for example, a `Stream` transit for binary file payloads alongside a `Message` transit for control commands.

## Transports

A transport supplies the underlying `IStreamPair` to the multiplexer. Pick exactly one per multiplexer instance.

| Package | Underlying API | Best for |
| --- | --- | --- |
| [`NetConduit.Transport.Tcp`](https://www.nuget.org/packages/NetConduit.Transport.Tcp) | `TcpClient` / `TcpListener` | General-purpose server/client over LAN/WAN |
| [`NetConduit.Transport.WebSocket`](https://www.nuget.org/packages/NetConduit.Transport.WebSocket) | `ClientWebSocket` / server `WebSocket` | Browser clients, firewall traversal, ASP.NET hosts |
| [`NetConduit.Transport.Udp`](https://www.nuget.org/packages/NetConduit.Transport.Udp) | `UdpClient` + reliable shim | Environments where TCP is unavailable |
| [`NetConduit.Transport.Ipc`](https://www.nuget.org/packages/NetConduit.Transport.Ipc) | TCP loopback (Windows) / Unix domain sockets (Linux/macOS) | Same-machine processes |
| [`NetConduit.Transport.Quic`](https://www.nuget.org/packages/NetConduit.Transport.Quic) | `System.Net.Quic` | Modern QUIC over TLS 1.3; requires OS support |

See [Transports overview](transports/index.md) for a per-transport feature matrix.

## Dependency graph

```
        Your app
           |
       picks 1+ transits        picks exactly 1 transport      picks mesh
           |                                |                      |
   +-------+--------+              +--------+-------+              |
   |               |              |               |                |
Transit.Stream    Transit.Message Transport.Tcp   Transport.Quic   NetConduit.Mesh
Transit.Duplex…   Transit.Delta…  Transport.WebS… Transport.Ipc     |
                                  Transport.Udp                     |
           \              |             /                          /
            \             |            /                          /
             \            |           /                          /
              +-----> NetConduit <----+-------------------------+
                       (core)
```

The core package has no third-party dependencies. `NetConduit.Mesh` depends only on the core. Each transport package uses only `System.*` libraries (with QUIC requiring OS QUIC support via `System.Net.Quic`). Each transit depends only on the core and `System.Text.Json`.
