# Samples

The `samples/` folder ships seven runnable programs exercising NetConduit end-to-end. Each is a single-file `Program.cs` you can clone and adapt.

| Sample | Demonstrates | Transport | Transit |
| --- | --- | --- | --- |
| [FileTransferSample](../../samples/FileTransferSample/README.md) | Parallel multi-file transfer. | TCP | `StreamTransit`, `MessageTransit` |
| [GroupChatSample](../../samples/GroupChatSample/README.md) | N-client broadcast chat over TCP **or** WebSocket. | TCP / WebSocket | `MessageTransit` |
| [PongGame](../../samples/PongGame/README.md) | Real-time game with delta state sync and input streaming. | TCP | `MessageTransit`, `DeltaMessageTransit` |
| [RemoteShellSample](../../samples/RemoteShellSample/README.md) | SSH-like remote shell with persistent process. | TCP | `MessageTransit`, `DuplexStreamTransit` |
| [RpcFrameworkSample](../../samples/RpcFrameworkSample/README.md) | Typed RPC request/response over `MessageTransit`. | TCP | `MessageTransit` |
| [ScoreboardSample](../../samples/ScoreboardSample/README.md) | Persistent leaderboard with custom reconnecting `StreamFactory`. | TCP (custom factory) | `MessageTransit`, `DeltaMessageTransit` |
| [SimpleTcpTunnel](../../samples/SimpleTcpTunnel/README.md) | TCP tunneling via relay + agent + forward roles. | TCP and WebSocket | `DuplexStreamTransit`, `MessageTransit` |

## Running

From the repo root:

```powershell
dotnet run --project samples/FileTransferSample -- server 5001 ./downloads
dotnet run --project samples/FileTransferSample -- send   5001 127.0.0.1 file.bin
```

Each sample prints its full usage when invoked without arguments.
